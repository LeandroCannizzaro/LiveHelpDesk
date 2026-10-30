using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.State;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Core.Delivery;

public sealed record DispatcherOptions
{
    /// <summary>Channels shown by the desktop app itself; the service never sends these.</summary>
    public IReadOnlySet<string> AppChannels { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "toast", "overlay" };

    public int MaxAttempts { get; init; } = 8;

    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LongestRetry { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Consecutive failures after which a channel is left alone for a while.</summary>
    public int BreakerThreshold { get; init; } = 5;

    public TimeSpan BreakerCooldown { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan BreakerMaxCooldown { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How many messages a channel may send per hour; the rest waits or is folded into one summary.</summary>
    public int MaxPerHour { get; init; } = 20;

    public IReadOnlyDictionary<string, int> MaxPerHourByChannel { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int BatchSize { get; init; } = 100;

    public int LimitFor(string channel) => MaxPerHourByChannel.TryGetValue(channel, out var limit) ? limit : MaxPerHour;
}

public sealed record ChannelStatus(
    string Id,
    bool Configured,
    BreakerState Breaker,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure,
    string? LastError,
    int SentLastHour);

/// <param name="Health">Problems worth telling the user about (a channel started failing, or recovered).</param>
public sealed record DispatchSummary(int Sent, int Failed, int Dead, int Cancelled, int Digests, IReadOnlyList<TicketEvent> Health);

/// <summary>
/// Sends what the rules queued, through the channels the service owns. Never loses a message to a hiccup:
/// failures are retried with growing waits, a channel that keeps failing is left alone for a while, and a
/// burst is folded into one summary rather than flooding a phone.
/// </summary>
public sealed class OutboxDispatcher(
    IOutbox outbox,
    ITicketStateStore store,
    Func<string, INotificationChannel?> channelFor,
    DispatcherOptions options,
    ILogger logger,
    Func<string, int>? limitFor = null)
{
    private sealed class ChannelState(DispatcherOptions options)
    {
        public CircuitBreaker Breaker { get; } = new(options.BreakerThreshold, options.BreakerCooldown, options.BreakerMaxCooldown);

        public List<DateTimeOffset> SentTimes { get; } = [];

        public DateTimeOffset? LastSuccess { get; set; }

        public DateTimeOffset? LastFailure { get; set; }

        public string? LastError { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChannelState> _states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ChannelStatus> Status(DateTimeOffset now) =>
        [.. _states.Select(kv => new ChannelStatus(
            kv.Key, channelFor(kv.Key) is not null, kv.Value.Breaker.State, kv.Value.LastSuccess, kv.Value.LastFailure, kv.Value.LastError,
            SentInLastHour(kv.Value, now)))];

    public async Task<DispatchSummary> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        int sent = 0, failed = 0, dead = 0, cancelled = 0, digests = 0;
        var health = new List<TicketEvent>();

        var due = outbox.DueExcluding(options.AppChannels, now, options.BatchSize);
        foreach (var group in due.GroupBy(i => i.Channel, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channelFor(group.Key);
            var state = State(group.Key);

            if (channel is null)
            {
                foreach (var item in group)
                {
                    outbox.MarkCancelled(item.Id, "canale non configurato o disattivato", now);
                    cancelled++;
                }

                continue;
            }

            var items = new List<OutboxItem>();
            foreach (var item in group)
            {
                if (item.RequiresUnacknowledged && store.IsEventAcknowledged(item.EventKey))
                {
                    outbox.MarkCancelled(item.Id, "già riconosciuto", now);
                    cancelled++;
                }
                else
                {
                    items.Add(item);
                }
            }

            var quota = Math.Max(0, (limitFor ?? options.LimitFor)(group.Key) - SentInLastHour(state, now));
            if (quota == 0)
            {
                continue; // stays queued until the hour frees a slot
            }

            // More than the hour allows: send the first ones as they are, fold the rest into a single summary.
            var individual = items.Count <= quota ? items : items.Take(quota - 1).ToList();
            var folded = items.Skip(individual.Count).ToList();

            foreach (var item in individual)
            {
                if (!state.Breaker.Allow(now))
                {
                    break;
                }

                var result = await TrySendAsync(channel, item.Notification, cancellationToken);
                if (result.Success)
                {
                    outbox.MarkSent(item.Id, now);
                    sent++;
                    Succeeded(group.Key, state, now, health);
                }
                else
                {
                    var gaveUp = item.Attempts + 1 >= options.MaxAttempts;
                    outbox.MarkFailed(item.Id, result.Error ?? "errore", now + RetryAfter(item.Attempts + 1), gaveUp);
                    failed++;
                    dead += gaveUp ? 1 : 0;
                    Failed(group.Key, state, now, result.Error, health);
                }
            }

            if (folded.Count > 0 && state.Breaker.Allow(now))
            {
                var summary = Digest(folded);
                var result = await TrySendAsync(channel, summary, cancellationToken);
                if (result.Success)
                {
                    foreach (var item in folded)
                    {
                        outbox.MarkSent(item.Id, now);
                    }

                    sent += folded.Count;
                    digests++;
                    Succeeded(group.Key, state, now, health);
                }
                else
                {
                    failed += folded.Count;
                    Failed(group.Key, state, now, result.Error, health);
                }
            }
        }

        return new DispatchSummary(sent, failed, dead, cancelled, digests, health);
    }

    /// <summary>One message standing for many, so a burst of tickets does not become a burst of text messages.</summary>
    private static Notification Digest(IReadOnlyList<OutboxItem> items)
    {
        var lines = items.Take(10).Select(i => $"• {i.Notification.Title}");
        var more = items.Count > 10 ? $"\n…e altre {items.Count - 10}" : "";
        return new Notification(
            $"{items.Count} notifiche da LiveHelpDesk",
            string.Join("\n", lines) + more,
            items.Max(i => i.Notification.Severity));
    }

    private async Task<SendResult> TrySendAsync(INotificationChannel channel, Notification notification, CancellationToken cancellationToken)
    {
        try
        {
            return await channel.SendAsync(notification, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A channel that throws is a channel that failed: it must not take the others down with it.
            logger.LogWarning(ex, "Channel {Channel} threw while sending", channel.Id);
            return SendResult.Fail(ex.Message);
        }
    }

    private void Succeeded(string channel, ChannelState state, DateTimeOffset now, List<TicketEvent> health)
    {
        lock (state.SentTimes)
        {
            state.SentTimes.Add(now);
        }

        state.LastSuccess = now;
        state.LastError = null;
        if (state.Breaker.RecordSuccess() == BreakerChange.Recovered)
        {
            logger.LogInformation("Channel {Channel} works again", channel);
            health.Add(Health("ChannelRecovered", channel, "di nuovo operativo", now));
        }
    }

    private void Failed(string channel, ChannelState state, DateTimeOffset now, string? error, List<TicketEvent> health)
    {
        state.LastFailure = now;
        state.LastError = error;
        logger.LogWarning("Channel {Channel} failed: {Error}", channel, error);
        if (state.Breaker.RecordFailure(now) == BreakerChange.Opened)
        {
            health.Add(Health("ChannelFailing", channel, error ?? "errore", now));
        }
    }

    private static TicketEvent Health(string subtype, string channel, string detail, DateTimeOffset now) =>
        new($"health:{subtype}:{channel}:{now.UtcTicks}", TicketEventType.MonitorHealth, now, Subtype: subtype, Change: $"{channel}: {detail}");

    private static int SentInLastHour(ChannelState state, DateTimeOffset now)
    {
        lock (state.SentTimes)
        {
            state.SentTimes.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
            return state.SentTimes.Count;
        }
    }

    private ChannelState State(string channel)
    {
        return _states.GetOrAdd(channel, _ => new ChannelState(options));
    }

    private TimeSpan RetryAfter(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var wait = TimeSpan.FromTicks(Math.Min(options.FirstRetry.Ticks * (1L << exponent), options.LongestRetry.Ticks));
        return wait;
    }
}
