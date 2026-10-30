using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Delivery;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveHelpDesk.Tests;

internal sealed class FakeChannel(string id, Func<Notification, int, SendResult>? behaviour = null) : INotificationChannel
{
    public string Id => id;

    public List<Notification> Sent { get; } = [];

    public int Calls { get; private set; }

    public Task<SendResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        Calls++;
        var result = behaviour?.Invoke(notification, Calls) ?? SendResult.Ok;
        if (result.Success)
        {
            Sent.Add(notification);
        }

        return Task.FromResult(result);
    }

    public Task<SendResult> TestAsync(CancellationToken cancellationToken) => Task.FromResult(SendResult.Ok);
}

public class CircuitBreakerTests
{
    private static CircuitBreaker Breaker() => new(threshold: 3, cooldown: TimeSpan.FromMinutes(2), maxCooldown: TimeSpan.FromMinutes(8));

    [Fact]
    public void ItOpens_OnlyAfterTheThresholdOfConsecutiveFailures()
    {
        var breaker = Breaker();

        Assert.Equal(BreakerChange.None, breaker.RecordFailure(T.At(0)));
        Assert.Equal(BreakerChange.None, breaker.RecordFailure(T.At(0)));
        Assert.True(breaker.Allow(T.At(0)));
        Assert.Equal(BreakerChange.Opened, breaker.RecordFailure(T.At(0)));
        Assert.False(breaker.Allow(T.At(1)));
    }

    [Fact]
    public void ASuccess_ResetsTheCount()
    {
        var breaker = Breaker();
        breaker.RecordFailure(T.At(0));
        breaker.RecordFailure(T.At(0));
        breaker.RecordSuccess();

        breaker.RecordFailure(T.At(0));
        breaker.RecordFailure(T.At(0));

        Assert.True(breaker.Allow(T.At(0)));
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void AfterTheCooldown_OneProbeIsAllowed_AndASuccessClosesTheBreaker()
    {
        var breaker = Breaker();
        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure(T.At(0));
        }

        Assert.False(breaker.Allow(T.At(1)));
        Assert.True(breaker.Allow(T.At(2)));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
        Assert.False(breaker.Allow(T.At(2))); // only one probe at a time
        Assert.Equal(BreakerChange.Recovered, breaker.RecordSuccess());
        Assert.True(breaker.Allow(T.At(2)));
    }

    [Fact]
    public void AFailedProbe_DoublesTheCooldown_UpToTheMaximum()
    {
        var breaker = Breaker();
        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure(T.At(0));
        }

        Assert.True(breaker.Allow(T.At(2)));
        breaker.RecordFailure(T.At(2)); // now 4 minutes
        Assert.False(breaker.Allow(T.At(5)));
        Assert.True(breaker.Allow(T.At(6)));
        breaker.RecordFailure(T.At(6)); // 8 minutes
        Assert.False(breaker.Allow(T.At(13)));
        Assert.True(breaker.Allow(T.At(14)));
        breaker.RecordFailure(T.At(14)); // capped at 8
        Assert.True(breaker.Allow(T.At(22)));
    }
}

public sealed class OutboxDispatcherTests : IDisposable
{
    private readonly SqliteStateStore _store = new(":memory:");
    private readonly Dictionary<string, FakeChannel> _channels = [];
    private DispatcherOptions _options = new() { BreakerThreshold = 3, BreakerCooldown = TimeSpan.FromMinutes(2), MaxPerHour = 100 };

    public void Dispose() => _store.Dispose();

    private OutboxDispatcher Dispatcher() =>
        new(_store, _store, id => _channels.GetValueOrDefault(id), _options, NullLogger.Instance);

    private FakeChannel Channel(string id, Func<Notification, int, SendResult>? behaviour = null) =>
        _channels[id] = new FakeChannel(id, behaviour);

    private OutboxItem Add(string key, string channel = "email", int notBeforeMinutes = 0, bool requiresUnacknowledged = false, Severity severity = Severity.Attention, string? title = null)
    {
        var item = new OutboxItem(
            0, key, "rule", channel, new Notification(title ?? $"Titolo {key}", "corpo", severity), T.At(notBeforeMinutes),
            requiresUnacknowledged, OutboxStatus.Pending, 0, T.At(0));
        Assert.True(_store.TryAdd(item));
        return item;
    }

    private int Pending(string channel) => _store.PendingByChannel().GetValueOrDefault(channel);

    [Fact]
    public async Task DueItems_AreSent_AndLeaveTheQueue()
    {
        var email = Channel("email");
        Add("a");
        Add("b");

        var summary = await Dispatcher().RunOnceAsync(T.At(1), CancellationToken.None);

        Assert.Equal(2, summary.Sent);
        Assert.Equal(["Titolo a", "Titolo b"], email.Sent.Select(n => n.Title).ToArray());
        Assert.Equal(0, Pending("email"));
        Assert.Equal(2, _store.Counts()[OutboxStatus.Sent]);
    }

    [Fact]
    public async Task TheAppsOwnChannels_AreNeverTouchedByTheService()
    {
        var toast = Channel("toast");
        Add("a", channel: "toast");
        Add("b", channel: "overlay");

        var summary = await Dispatcher().RunOnceAsync(T.At(1), CancellationToken.None);

        Assert.Equal(0, summary.Sent);
        Assert.Equal(0, toast.Calls);
        Assert.Equal(1, Pending("toast"));
        Assert.Equal(1, Pending("overlay"));
    }

    [Fact]
    public async Task AppChannelItems_DoNotCrowdOutTheServicesOwn()
    {
        var email = Channel("email");
        _options = _options with { BatchSize = 3 };
        for (var i = 0; i < 10; i++)
        {
            Add($"t{i}", channel: "toast");
        }

        Add("mail");

        await Dispatcher().RunOnceAsync(T.At(1), CancellationToken.None);

        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task ItemsNotDueYet_AreLeftAlone()
    {
        var email = Channel("email");
        Add("later", notBeforeMinutes: 30);

        await Dispatcher().RunOnceAsync(T.At(1), CancellationToken.None);
        Assert.Equal(0, email.Calls);

        await Dispatcher().RunOnceAsync(T.At(31), CancellationToken.None);
        Assert.Equal(1, email.Calls);
    }

    [Fact]
    public async Task AFailure_IsRetriedLater_WithGrowingWaits()
    {
        var email = Channel("email", (_, _) => SendResult.Fail("timeout"));
        Add("a");
        var dispatcher = Dispatcher();

        await dispatcher.RunOnceAsync(T.At(0), CancellationToken.None);
        Assert.Empty(_store.Due(T.At(0).AddSeconds(29), 10));
        var second = Assert.Single(_store.Due(T.At(0).AddSeconds(30), 10));
        Assert.Equal(1, second.Attempts);

        await dispatcher.RunOnceAsync(T.At(0).AddSeconds(30), CancellationToken.None);
        Assert.Empty(_store.Due(T.At(0).AddSeconds(30 + 59), 10));
        Assert.Single(_store.Due(T.At(0).AddSeconds(30 + 60), 10));
        Assert.Equal(2, email.Calls);
    }

    [Fact]
    public async Task AfterTheMaximumAttempts_TheItemIsGivenUp()
    {
        Channel("email", (_, _) => SendResult.Fail("sempre no"));
        _options = _options with { MaxAttempts = 3, BreakerThreshold = 100, FirstRetry = TimeSpan.FromSeconds(1) };
        Add("a");
        var dispatcher = Dispatcher();

        DispatchSummary last = null!;
        for (var i = 0; i < 3; i++)
        {
            last = await dispatcher.RunOnceAsync(T.At(i * 10), CancellationToken.None);
        }

        Assert.Equal(1, last.Dead);
        Assert.Equal(1, _store.Counts()[OutboxStatus.Dead]);
        Assert.Empty(_store.Due(T.At(1000), 10));
    }

    [Fact]
    public async Task AChannelThatIsNotConfigured_CancelsItsItems_InsteadOfHoldingThemForever()
    {
        Add("a", channel: "sms");

        var summary = await Dispatcher().RunOnceAsync(T.At(1), CancellationToken.None);

        Assert.Equal(1, summary.Cancelled);
        Assert.Equal(1, _store.Counts()[OutboxStatus.Cancelled]);
    }

    [Fact]
    public async Task AnEscalation_IsDropped_IfTheEventWasAcknowledged_AndSentIfNot()
    {
        var sms = Channel("sms");
        _store.TryRegisterEvent(new TicketEvent("seen", TicketEventType.Unattended, T.At(0)));
        _store.TryRegisterEvent(new TicketEvent("ignored", TicketEventType.Unattended, T.At(0)));
        _store.AcknowledgeEvent("seen", T.At(5));
        Add("seen", channel: "sms", requiresUnacknowledged: true);
        Add("ignored", channel: "sms", requiresUnacknowledged: true);

        var summary = await Dispatcher().RunOnceAsync(T.At(10), CancellationToken.None);

        Assert.Equal(1, summary.Cancelled);
        Assert.Equal(["Titolo ignored"], sms.Sent.Select(n => n.Title).ToArray());
    }

    [Fact]
    public async Task AChannelThatThrows_FailsAlone_AndTheOthersStillSend()
    {
        Channel("sms", (_, _) => throw new InvalidOperationException("boom"));
        var email = Channel("email");
        Add("a", channel: "sms");
        Add("b", channel: "email");

        var summary = await Dispatcher().RunOnceAsync(T.At(1), CancellationToken.None);

        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.Failed);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task AFailingChannel_IsLeftAloneAfterTheThreshold_Reported_AndProbedAfterTheCooldown()
    {
        var healthy = false;
        var email = Channel("email", (_, _) => healthy ? SendResult.Ok : SendResult.Fail("server giù"));
        for (var i = 0; i < 6; i++)
        {
            Add($"m{i}");
        }

        var dispatcher = Dispatcher();

        var first = await dispatcher.RunOnceAsync(T.At(0), CancellationToken.None);
        Assert.Equal(3, email.Calls); // threshold reached: the other three are not even tried
        var failing = Assert.Single(first.Health);
        Assert.Equal("ChannelFailing", failing.Subtype);
        Assert.Contains("email", failing.Change);

        await dispatcher.RunOnceAsync(T.At(1), CancellationToken.None); // inside the cooldown
        Assert.Equal(3, email.Calls);

        healthy = true;
        var probe = await dispatcher.RunOnceAsync(T.At(3), CancellationToken.None); // cooldown over
        Assert.Equal("ChannelRecovered", Assert.Single(probe.Health).Subtype);
        Assert.True(email.Calls > 3);
        Assert.Equal(0, _store.Due(T.At(60), 100).Count(i => i.Channel == "email" && i.NotBefore <= T.At(3)));
    }

    [Fact]
    public async Task ABurst_IsFoldedIntoOneSummary_WhenTheHourlyLimitWouldBeExceeded()
    {
        var sms = Channel("sms");
        _options = _options with { MaxPerHourByChannel = new Dictionary<string, int> { ["sms"] = 3 } };
        for (var i = 1; i <= 6; i++)
        {
            Add($"t{i}", channel: "sms", severity: i == 5 ? Severity.Critical : Severity.Attention);
        }

        var summary = await Dispatcher().RunOnceAsync(T.At(0), CancellationToken.None);

        Assert.Equal(1, summary.Digests);
        Assert.Equal(3, sms.Sent.Count); // two single messages and one summary
        Assert.Equal("Titolo t1", sms.Sent[0].Title);
        Assert.Equal("Titolo t2", sms.Sent[1].Title);
        var digest = sms.Sent[2];
        Assert.Equal("4 notifiche da LiveHelpDesk", digest.Title);
        Assert.Contains("• Titolo t3", digest.Body);
        Assert.Contains("• Titolo t6", digest.Body);
        Assert.Equal(Severity.Critical, digest.Severity);
        Assert.Equal(0, Pending("sms")); // every original item is settled
    }

    [Fact]
    public async Task WhenTheLimitIsExhausted_ItemsWait_UntilTheHourFreesASlot()
    {
        var sms = Channel("sms");
        _options = _options with { MaxPerHourByChannel = new Dictionary<string, int> { ["sms"] = 2 } };
        var dispatcher = Dispatcher();
        Add("a", channel: "sms");
        Add("b", channel: "sms");
        await dispatcher.RunOnceAsync(T.At(0), CancellationToken.None);
        Add("c", channel: "sms");

        await dispatcher.RunOnceAsync(T.At(30), CancellationToken.None);
        Assert.Equal(1, Pending("sms"));
        Assert.Equal(2, sms.Sent.Count);

        await dispatcher.RunOnceAsync(T.At(61), CancellationToken.None);
        Assert.Equal(0, Pending("sms"));
        Assert.Equal(3, sms.Sent.Count);
    }

    [Fact]
    public async Task ASummary_ListsAtMostTenTitles()
    {
        var sms = Channel("sms");
        _options = _options with { MaxPerHourByChannel = new Dictionary<string, int> { ["sms"] = 1 } };
        for (var i = 1; i <= 14; i++)
        {
            Add($"t{i}", channel: "sms", title: $"T{i:00}");
        }

        await Dispatcher().RunOnceAsync(T.At(0), CancellationToken.None);

        var digest = Assert.Single(sms.Sent);
        Assert.Equal("14 notifiche da LiveHelpDesk", digest.Title);
        Assert.Contains("• T10", digest.Body);
        Assert.DoesNotContain("• T11", digest.Body);
        Assert.Contains("…e altre 4", digest.Body);
    }

    [Fact]
    public async Task Status_ReportsTheLastSuccessAndTheLastError()
    {
        var fail = false;
        Channel("email", (_, _) => fail ? SendResult.Fail("rifiutato") : SendResult.Ok);
        var dispatcher = Dispatcher();
        Add("a");
        await dispatcher.RunOnceAsync(T.At(1), CancellationToken.None);
        fail = true;
        Add("b");
        await dispatcher.RunOnceAsync(T.At(2), CancellationToken.None);

        var status = Assert.Single(dispatcher.Status(T.At(3)));

        Assert.Equal("email", status.Id);
        Assert.Equal(T.At(1), status.LastSuccess);
        Assert.Equal(T.At(2), status.LastFailure);
        Assert.Equal("rifiutato", status.LastError);
        Assert.Equal(1, status.SentLastHour);
    }

    [Fact]
    public async Task Cancellation_StopsTheRound()
    {
        Channel("email");
        Add("a");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Dispatcher().RunOnceAsync(T.At(1), cancelled.Token));
    }
}
