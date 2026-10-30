using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Sources;

namespace LiveHelpDesk.Core.Health;

/// <summary>
/// Makes sure the monitor never fails silently: turns a run of failed polls into one event per episode
/// (and a reminder if it lasts), and says so when things recover. Short blips stay quiet.
/// </summary>
public sealed class HealthMonitor(TimeSpan? unreachableGrace = null, TimeSpan? throttledGrace = null, TimeSpan? renotifyAfter = null)
{
    private readonly TimeSpan _unreachableGrace = unreachableGrace ?? TimeSpan.FromMinutes(5);
    private readonly TimeSpan _throttledGrace = throttledGrace ?? TimeSpan.FromMinutes(15);
    private readonly TimeSpan _renotifyAfter = renotifyAfter ?? TimeSpan.FromHours(6);

    private string? _kind;
    private DateTimeOffset _since;
    private DateTimeOffset? _lastNotified;

    public IReadOnlyList<TicketEvent> Failure(TicketSourceException failure, DateTimeOffset now)
    {
        var kind = failure.Failure switch
        {
            SourceFailure.AuthRequired => "AuthExpired",
            SourceFailure.Forbidden => "AccessDenied",
            SourceFailure.Throttled => "Throttled",
            SourceFailure.Error => "PollingError",
            _ => "DynamicsUnreachable",
        };

        if (_kind != kind)
        {
            // A different problem is a new episode, with its own grace period.
            _kind = kind;
            _since = now;
            _lastNotified = null;
        }

        var grace = kind switch
        {
            "Throttled" => _throttledGrace,
            "DynamicsUnreachable" => _unreachableGrace,
            _ => TimeSpan.Zero,
        };

        if (now - _since < grace || (_lastNotified is { } last && now - last < _renotifyAfter))
        {
            return [];
        }

        _lastNotified = now;
        return [Event(kind, now, failure.Message)];
    }

    public IReadOnlyList<TicketEvent> Success(DateTimeOffset now)
    {
        var wasNotified = _lastNotified is not null;
        var kind = _kind;
        _kind = null;
        _lastNotified = null;
        return wasNotified ? [Event("Recovered", now, $"Dopo: {kind}")] : [];
    }

    private static TicketEvent Event(string subtype, DateTimeOffset now, string detail) =>
        new($"health:{subtype}:{now.UtcTicks}", TicketEventType.MonitorHealth, now, Subtype: subtype, Change: detail);
}

/// <summary>How long to wait before the next poll.</summary>
public static class PollingSchedule
{
    public static TimeSpan AfterSuccess(TimeSpan interval, double jitter = 0) =>
        interval + TimeSpan.FromTicks((long)(interval.Ticks * 0.1 * Math.Clamp(jitter, 0, 1)));

    public static TimeSpan AfterFailure(TicketSourceException failure, int consecutiveFailures) =>
        failure.Failure switch
        {
            SourceFailure.Throttled => failure.RetryAfter ?? Backoff(consecutiveFailures, TimeSpan.FromSeconds(30)),
            SourceFailure.AuthRequired => TimeSpan.FromMinutes(1),
            SourceFailure.Forbidden => TimeSpan.FromMinutes(5),
            _ => Backoff(consecutiveFailures, TimeSpan.FromSeconds(10)),
        };

    private static TimeSpan Backoff(int failures, TimeSpan first)
    {
        var exponent = Math.Clamp(failures - 1, 0, 10);
        var delay = TimeSpan.FromTicks(first.Ticks * (1L << exponent));
        return delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay;
    }
}
