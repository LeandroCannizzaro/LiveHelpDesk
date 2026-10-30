using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Detection;
using LiveHelpDesk.Core.Notifications;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Polling;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Core.Sources;
using LiveHelpDesk.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveHelpDesk.Tests;

/// <summary>An in-memory Dynamics: tests change its contents and its clock, then poll.</summary>
internal sealed class FakeSource : ITicketSource
{
    public DateTimeOffset Now { get; set; } = T.Baseline;

    public List<TicketSnapshot> Tickets { get; } = [];

    public List<MessageSnapshot> Messages { get; } = [];

    public Exception? Fail { get; set; }

    private void Check()
    {
        if (Fail is not null)
        {
            throw Fail;
        }
    }

    public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken)
    {
        Check();
        return Task.FromResult(Now);
    }

    public Task<IReadOnlyList<TicketSnapshot>> GetActiveTicketsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TicketSnapshot>>(Tickets.Where(t => t.State == 0).ToList());

    public Task<IReadOnlyList<Guid>> GetActiveTicketIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Tickets.Where(t => t.State == 0).Select(t => t.Id).ToList());

    public Task<IReadOnlyList<TicketSnapshot>> GetTicketsModifiedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TicketSnapshot>>(Tickets.Where(t => t.ModifiedOn >= since).ToList());

    public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TicketSnapshot>>(Tickets.Where(t => ids.Contains(t.Id)).ToList());

    public Task<IReadOnlyList<MessageSnapshot>> GetMessagesCreatedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessageSnapshot>>(Messages.Where(m => m.CreatedOn >= since).ToList());
}

public sealed class PollingEngineTests : IDisposable
{
    private readonly FakeSource _source = new();
    private readonly SqliteStateStore _store = new(":memory:");
    private IReadOnlyList<Rule> _rules = DefaultRules.Create().Rules;
    private readonly PollingEngine _engine;

    public PollingEngineTests()
    {
        var mapping = TicketMapping.Qgs;
        var planner = new NotificationPlanner(mapping, new ChannelPolicy(), id => new Uri($"https://crm.example/{id:D}"));
        _engine = new PollingEngine(
            _source, _store, _store, new TicketDetector(_store, mapping), planner, mapping, new PollingOptions(),
            () => _rules, () => T.Id(900), NullLogger.Instance);
    }

    public void Dispose() => _store.Dispose();

    private async Task<PollResult> PollAt(int minutes)
    {
        _source.Now = T.At(minutes);
        return await _engine.PollOnceAsync(CancellationToken.None);
    }

    private async Task StartWith(params TicketSnapshot[] tickets)
    {
        _source.Tickets.AddRange(tickets);
        await PollAt(0);
    }

    private int Queued => _store.Due(DateTimeOffset.MaxValue, 1000).Count;

    [Fact]
    public async Task TheFirstPoll_TakesTheBaseline_AndNotifiesAboutNoTicket()
    {
        _source.Tickets.AddRange([T.Ticket(1), T.Ticket(2), T.Ticket(3)]);

        var result = await PollAt(0);

        Assert.True(result.BaselineTaken);
        Assert.Equal(0, Queued);
        Assert.Equal(3, _store.TicketsInState(0).Count);
        Assert.Equal(T.At(0), _store.BaselineAt);
    }

    [Fact]
    public async Task ANewTicket_IsQueuedOnEveryChannelOfTheRule_WithALinkToIt()
    {
        await StartWith(T.Ticket(1));
        var fresh = T.Ticket(2, created: T.At(3), priority: 1);
        _source.Tickets.Add(fresh);

        var result = await PollAt(5);

        Assert.Equal(2, result.Queued);
        var items = _store.Due(T.At(5), 10);
        Assert.Equal(["toast", "overlay"], items.Select(i => i.Channel).ToArray());
        Assert.All(items, i =>
        {
            Assert.Equal($"Nuovo ticket #{fresh.Number}", i.Notification.Title);
            Assert.Equal(new Uri($"https://crm.example/{fresh.Id:D}"), i.Notification.Url);
            Assert.Equal(Severity.Attention, i.Notification.Severity);
        });
    }

    [Fact]
    public async Task ReadingTheSameChangeAgain_QueuesNothingNew()
    {
        await StartWith(T.Ticket(1));
        _source.Tickets.Add(T.Ticket(2, created: T.At(3)));

        await PollAt(5);
        var again = await PollAt(6); // still inside the overlap window: the ticket is read a second time

        Assert.Empty(again.Events);
        Assert.Equal(0, again.Queued);
        Assert.Equal(2, Queued);
    }

    [Fact]
    public async Task ACustomerEmail_OnAKnownTicket_IsQueued()
    {
        await StartWith(T.Ticket(1));
        _source.Messages.Add(T.Message(1, 1, atMinutes: 4));

        var result = await PollAt(5);

        Assert.Equal(2, result.Queued);
        Assert.Equal("Nuova e-mail su #261000001", _store.Due(T.At(5), 1)[0].Notification.Title);
    }

    [Fact]
    public async Task OurOwnReply_RaisesAnEvent_ButNoDefaultRuleNotifiesForIt()
    {
        await StartWith(T.Ticket(1));
        _source.Messages.Add(T.Message(1, 1, incoming: false, atMinutes: 4));

        var result = await PollAt(5);

        Assert.Contains(result.Events, e => e.Type == TicketEventType.ReplySent);
        Assert.Equal(0, result.Queued);
    }

    [Fact]
    public async Task AMessageForATicketNotSeenBefore_FetchesTheTicketAndStillNotifies()
    {
        await StartWith();
        // An old, quiet ticket: not in "modified since", so the poll only knows it through the message.
        _source.Tickets.Add(T.Ticket(9, modified: T.Baseline.AddDays(-5)));
        _source.Messages.Add(T.Message(1, 9, atMinutes: 4));

        var result = await PollAt(5);

        Assert.Contains(result.Events, e => e.Type == TicketEventType.MessageReceived && e.TicketNumber == "261000009");
        Assert.Equal(2, result.Queued);
    }

    [Fact]
    public async Task AfterALongBlindSpell_ItRealignsInsteadOfReplayingHours_AndSaysSo()
    {
        await StartWith(T.Ticket(1));
        for (var i = 2; i <= 12; i++)
        {
            _source.Tickets.Add(T.Ticket(i, created: T.At(60 + i)));
        }

        var result = await PollAt(7 * 60); // more than the 6 hour limit

        Assert.True(result.BaselineTaken);
        Assert.DoesNotContain(result.Events, e => e.Type == TicketEventType.TicketCreated);
        Assert.Contains(result.Events, e => e.Subtype == "GapDetected");
        Assert.Equal(12, _store.TicketsInState(0).Count);
        Assert.Equal(2, result.Queued); // only the health notification
    }

    [Fact]
    public async Task ATicketThatVanishesFromTheServer_IsReportedOnceByTheReconciliation()
    {
        await StartWith(T.Ticket(1), T.Ticket(2));
        _source.Tickets.RemoveAll(t => t.Id == T.Id(1));

        var early = await PollAt(5); // reconciliation is not due yet
        var due = await PollAt(20);
        var later = await PollAt(40);

        Assert.Empty(early.Events);
        var gone = Assert.Single(due.Events);
        Assert.Equal("Disappeared", gone.Subtype);
        Assert.Equal("261000001", gone.TicketNumber);
        Assert.Empty(later.Events);
        Assert.Null(_store.GetTicket(T.Id(1)));
    }

    [Fact]
    public async Task AnActiveTicketThatSlippedThroughTheWatermark_IsFoundByTheReconciliation()
    {
        await StartWith(T.Ticket(1));
        // Created after the baseline but stamped far in the past: the "modified since" query can never return it.
        _source.Tickets.Add(T.Ticket(2, created: T.At(2), modified: T.Baseline.AddDays(-1)));

        var result = await PollAt(20);

        var created = Assert.Single(result.Events, e => e.Type == TicketEventType.TicketCreated);
        Assert.Equal("261000002", created.TicketNumber);
    }

    [Fact]
    public async Task ANewTicketNobodyAnswers_BecomesUnattendedAfterTheThreshold()
    {
        await StartWith();
        _source.Tickets.Add(T.Ticket(1, created: T.At(1)));
        await PollAt(2);

        var result = await PollAt(40);

        var unattended = Assert.Single(result.Events, e => e.Type == TicketEventType.Unattended);
        Assert.Equal("261000001", unattended.TicketNumber);
        Assert.Contains(_store.Due(T.At(40), 50), i => i.Notification.Severity == Severity.Critical);
    }

    [Fact]
    public async Task TheWatermarks_AdvanceToJustBehindTheServerClock()
    {
        await StartWith(T.Ticket(1));

        await PollAt(30);

        Assert.Equal(T.At(28).ToString("O"), DateTimeOffset.Parse(_store.GetValue("wm.incident")!).ToString("O"));
        Assert.Equal(T.At(28).ToString("O"), DateTimeOffset.Parse(_store.GetValue("wm.message")!).ToString("O"));
    }

    [Fact]
    public async Task AFailingSource_SurfacesTheError_AndLeavesTheStateAlone()
    {
        await StartWith(T.Ticket(1));
        var before = _store.GetValue("wm.incident");
        _source.Fail = new TicketSourceException(SourceFailure.AuthRequired, "login");

        var ex = await Assert.ThrowsAsync<TicketSourceException>(() => PollAt(10));

        Assert.Equal(SourceFailure.AuthRequired, ex.Failure);
        Assert.Equal(before, _store.GetValue("wm.incident"));
    }

    [Fact]
    public async Task HealthEvents_GoThroughTheSameRules()
    {
        await StartWith();
        var health = new TicketEvent("health:AuthExpired:1", TicketEventType.MonitorHealth, T.At(1), Subtype: "AuthExpired", Change: "login");

        var queued = _engine.Publish([health], T.At(1));

        Assert.Equal(2, queued);
        Assert.Equal("LiveHelpDesk: accesso a Dynamics scaduto", _store.Due(T.At(1), 1)[0].Notification.Title);
    }

    [Fact]
    public async Task TheBaselineAndRecoveryEvents_StayQuietByDefault()
    {
        await StartWith();

        Assert.Equal(0, Queued); // BaselineCompleted matches no default rule
    }

    [Fact]
    public async Task AnEscalationIsScheduledForLater_AndOnlyIfNobodyAcknowledges()
    {
        _rules = RulesConfig.Parse(
            """
            { "version": 1, "rules": [ { "id": "r", "events": ["TicketCreated"],
              "actions": { "channels": ["toast"], "severity": "Critical", "escalateAfterMinutes": 10, "escalateChannels": ["sms", "toast"] } } ] }
            """).File!.Rules;
        await StartWith();
        _source.Tickets.Add(T.Ticket(1, created: T.At(1)));

        await PollAt(2);

        var items = _store.Due(DateTimeOffset.MaxValue, 10);
        Assert.Equal(["toast", "sms"], items.Select(i => i.Channel).ToArray()); // toast is not repeated as an escalation
        var escalation = items.Single(i => i.Channel == "sms");
        Assert.Equal(T.At(12), escalation.NotBefore);
        Assert.True(escalation.RequiresUnacknowledged);
        Assert.False(items.Single(i => i.Channel == "toast").RequiresUnacknowledged);
    }

    [Fact]
    public async Task EditingTheRulesBetweenPolls_TakesEffectImmediately()
    {
        await StartWith();
        _rules = [];
        _source.Tickets.Add(T.Ticket(1, created: T.At(1)));
        await PollAt(2);
        Assert.Equal(0, Queued);

        _rules = DefaultRules.Create().Rules;
        _source.Tickets.Add(T.Ticket(2, created: T.At(3)));
        await PollAt(4);

        Assert.Equal(2, Queued);
    }
}
