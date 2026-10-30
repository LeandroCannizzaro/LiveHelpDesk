using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Health;
using LiveHelpDesk.Core.Notifications;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Core.Sources;
using LiveHelpDesk.Core.State;
using Xunit;

namespace LiveHelpDesk.Tests;

public class NotificationTextTests
{
    private static readonly TicketMapping Mapping = TicketMapping.Qgs;

    private static TicketEvent Event(TicketEventType type, string? subtype = null, string? change = null, TicketSnapshot? ticket = null, MessageSnapshot? message = null) =>
        new("k", type, T.At(0), T.Id(1), "261000001", "Errore impegno", subtype, change, ticket ?? T.Ticket(priority: 1), message);

    [Theory]
    [InlineData(TicketEventType.TicketCreated, null, "Nuovo ticket #261000001")]
    [InlineData(TicketEventType.MessageReceived, "Email", "Nuova e-mail su #261000001")]
    [InlineData(TicketEventType.MessageReceived, "Note", "Nuova nota su #261000001")]
    [InlineData(TicketEventType.CustomerReplied, null, "Il cliente ha risposto su #261000001")]
    [InlineData(TicketEventType.StatusChanged, "Reopened", "Ticket #261000001 riaperto")]
    [InlineData(TicketEventType.StatusChanged, "Closed", "Ticket #261000001 chiuso")]
    [InlineData(TicketEventType.StatusChanged, "Disappeared", "Ticket #261000001 non più visibile")]
    [InlineData(TicketEventType.PriorityChanged, "Increased", "Ticket #261000001: priorità aumentata")]
    [InlineData(TicketEventType.Unattended, null, "Ticket #261000001 senza risposta")]
    [InlineData(TicketEventType.TicketReassigned, "Unassigned", "Ticket #261000001 senza assegnatario")]
    public void Titles_AreShortItalianSentences(TicketEventType type, string? subtype, string expected) =>
        Assert.Equal(expected, NotificationText.Describe(Event(type, subtype), Mapping, DetailLevel.Full).Title);

    [Fact]
    public void FullDetail_NamesPriorityAndStatus_AndTheTransition()
    {
        var evt = Event(TicketEventType.StatusChanged, "StepChanged", "717600002->717600003", T.Ticket(priority: 1, status: 717600003, title: "Errore impegno"));

        var (_, body) = NotificationText.Describe(evt, Mapping, DetailLevel.Full);

        Assert.Contains("Errore impegno", body);
        Assert.Contains("Priorità: Alta · Stato: L1 In risoluzione", body);
        Assert.Contains("L1 Assegnazione → L1 In risoluzione", body);
    }

    [Fact]
    public void MessageDetail_ShowsSenderAndSubject()
    {
        var message = new MessageSnapshot(T.Id(5), T.Id(1), MessageKind.Email, true, T.At(0), "mario@cliente.it", "Fattura errata");

        var (_, body) = NotificationText.Describe(Event(TicketEventType.MessageReceived, "Email", message: message), Mapping, DetailLevel.Full);

        Assert.Contains("Da: mario@cliente.it — Fattura errata", body);
    }

    [Fact]
    public void MinimalDetail_LeavesOutTheTitleAndTheSubject()
    {
        var message = new MessageSnapshot(T.Id(5), T.Id(1), MessageKind.Email, true, T.At(0), "mario@cliente.it", "Fattura errata");

        var (_, body) = NotificationText.Describe(Event(TicketEventType.MessageReceived, "Email", message: message), Mapping, DetailLevel.Minimal);

        Assert.Equal("#261000001 · Priorità Alta", body);
        Assert.DoesNotContain("Errore", body);
        Assert.DoesNotContain("mario", body);
    }

    [Fact]
    public void HealthEvents_CarryTheirReason()
    {
        var evt = new TicketEvent("k", TicketEventType.MonitorHealth, T.At(0), Subtype: "DynamicsUnreachable", Change: "timeout");

        var (title, body) = NotificationText.Describe(evt, Mapping, DetailLevel.Full);

        Assert.Equal("LiveHelpDesk: Dynamics non raggiungibile", title);
        Assert.Equal("timeout", body);
    }
}

public class NotificationPlannerTests
{
    private static RuleMatch Match(RuleActions actions) =>
        new(
            new Rule { Id = "r", Events = [TicketEventType.TicketCreated], Actions = actions },
            new TicketEvent("k", TicketEventType.TicketCreated, T.At(0), T.Id(1), "261000001", "t", Ticket: T.Ticket()));

    [Fact]
    public void QuietHours_TurnAChannelIntoASuppressedRow_NotIntoSilence()
    {
        var policy = new ChannelPolicy
        {
            Channels = { ["sms"] = new ChannelSchedule { Windows = [new ChannelWindow { From = new TimeOnly(2, 0), To = new TimeOnly(2, 1) }] } },
        };
        var planner = new NotificationPlanner(TicketMapping.Qgs, () => policy, _ => null);

        var items = planner.Plan(Match(new RuleActions { Channels = ["toast", "sms"] }), new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(OutboxStatus.Pending, items.Single(i => i.Channel == "toast").Status);
        Assert.Equal(OutboxStatus.Suppressed, items.Single(i => i.Channel == "sms").Status);
    }

    [Fact]
    public void DuplicateChannels_AreOnlyUsedOnce()
    {
        var planner = new NotificationPlanner(TicketMapping.Qgs, () => new ChannelPolicy(), _ => null);

        var items = planner.Plan(Match(new RuleActions { Channels = ["toast", "TOAST"] }), T.At(0));

        Assert.Single(items);
    }
}

public class HealthMonitorTests
{
    private static TicketSourceException Failure(SourceFailure kind) => new(kind, "boom");

    [Fact]
    public void AnExpiredLogin_IsReportedAtOnce_AndNotRepeatedEveryMinute()
    {
        var monitor = new HealthMonitor();

        var first = monitor.Failure(Failure(SourceFailure.AuthRequired), T.At(0));
        var second = monitor.Failure(Failure(SourceFailure.AuthRequired), T.At(1));

        Assert.Equal("AuthExpired", Assert.Single(first).Subtype);
        Assert.Empty(second);
    }

    [Fact]
    public void ALongOutage_IsReportedAgainAfterTheReminderInterval()
    {
        var monitor = new HealthMonitor(renotifyAfter: TimeSpan.FromHours(6));
        monitor.Failure(Failure(SourceFailure.AuthRequired), T.At(0));

        Assert.Empty(monitor.Failure(Failure(SourceFailure.AuthRequired), T.At(300)));
        Assert.Single(monitor.Failure(Failure(SourceFailure.AuthRequired), T.At(361)));
    }

    [Fact]
    public void AShortNetworkBlip_StaysQuiet_ButALongOneIsReported()
    {
        var monitor = new HealthMonitor(unreachableGrace: TimeSpan.FromMinutes(5));

        Assert.Empty(monitor.Failure(Failure(SourceFailure.Unavailable), T.At(0)));
        Assert.Empty(monitor.Failure(Failure(SourceFailure.Unavailable), T.At(3)));
        Assert.Equal("DynamicsUnreachable", Assert.Single(monitor.Failure(Failure(SourceFailure.Unavailable), T.At(6))).Subtype);
    }

    [Fact]
    public void ThrottlingIsToleratedForAWhile()
    {
        var monitor = new HealthMonitor(throttledGrace: TimeSpan.FromMinutes(15));

        Assert.Empty(monitor.Failure(Failure(SourceFailure.Throttled), T.At(0)));
        Assert.Single(monitor.Failure(Failure(SourceFailure.Throttled), T.At(16)));
    }

    [Theory]
    [InlineData(SourceFailure.Forbidden, "AccessDenied")]
    [InlineData(SourceFailure.Error, "PollingError")]
    public void PermissionAndProtocolProblems_AreReportedImmediately(SourceFailure kind, string subtype) =>
        Assert.Equal(subtype, Assert.Single(new HealthMonitor().Failure(Failure(kind), T.At(0))).Subtype);

    [Fact]
    public void Recovery_IsAnnouncedOnlyIfTheProblemWasAnnounced()
    {
        var monitor = new HealthMonitor();
        monitor.Failure(Failure(SourceFailure.Unavailable), T.At(0)); // within grace: nothing announced

        Assert.Empty(monitor.Success(T.At(1)));

        monitor.Failure(Failure(SourceFailure.AuthRequired), T.At(2));
        var recovered = monitor.Success(T.At(3));

        Assert.Equal("Recovered", Assert.Single(recovered).Subtype);
        Assert.Empty(monitor.Success(T.At(4)));
    }

    [Fact]
    public void ADifferentProblem_StartsANewEpisode()
    {
        var monitor = new HealthMonitor(unreachableGrace: TimeSpan.FromMinutes(5));
        monitor.Failure(Failure(SourceFailure.AuthRequired), T.At(0));

        var next = monitor.Failure(Failure(SourceFailure.Forbidden), T.At(1));

        Assert.Equal("AccessDenied", Assert.Single(next).Subtype);
    }
}

public class PollingScheduleTests
{
    [Fact]
    public void AfterSuccess_AddsUpToTenPercentJitter()
    {
        var interval = TimeSpan.FromSeconds(30);

        Assert.Equal(interval, PollingSchedule.AfterSuccess(interval, 0));
        Assert.Equal(TimeSpan.FromSeconds(33), PollingSchedule.AfterSuccess(interval, 1));
    }

    [Fact]
    public void Throttling_HonoursRetryAfter() =>
        Assert.Equal(
            TimeSpan.FromSeconds(42),
            PollingSchedule.AfterFailure(new TicketSourceException(SourceFailure.Throttled, "x", TimeSpan.FromSeconds(42)), 1));

    [Fact]
    public void Outages_BackOffExponentially_UpToFiveMinutes()
    {
        var down = new TicketSourceException(SourceFailure.Unavailable, "x");

        Assert.Equal(TimeSpan.FromSeconds(10), PollingSchedule.AfterFailure(down, 1));
        Assert.Equal(TimeSpan.FromSeconds(20), PollingSchedule.AfterFailure(down, 2));
        Assert.Equal(TimeSpan.FromSeconds(40), PollingSchedule.AfterFailure(down, 3));
        Assert.Equal(TimeSpan.FromMinutes(5), PollingSchedule.AfterFailure(down, 50));
    }

    [Fact]
    public void ALoginProblem_IsRetriedEveryMinute_SoANewLoginIsPickedUpQuickly() =>
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            PollingSchedule.AfterFailure(new TicketSourceException(SourceFailure.AuthRequired, "x"), 9));
}

public class DefaultRulesTests
{
    [Fact]
    public void TheStartingRules_AreValid_AndSurviveARoundTrip()
    {
        var rules = DefaultRules.Create();

        Assert.Empty(RulesConfig.Validate(rules));
        var again = RulesConfig.Parse(RulesConfig.Serialize(rules));
        Assert.True(again.IsValid, string.Join("; ", again.Errors));
        Assert.Equal(rules.Rules.Select(r => r.Id), again.File!.Rules.Select(r => r.Id));
    }
}

public sealed class OutboxTests : IDisposable
{
    private readonly SqliteStateStore _store = new(":memory:");

    public void Dispose() => _store.Dispose();

    private static OutboxItem Item(string key = "e1", string channel = "toast", int notBeforeMinutes = 0, OutboxStatus status = OutboxStatus.Pending) =>
        new(0, key, "rule", channel, new Notification("t", "b", Severity.Attention, new Uri("https://x/y")), T.At(notBeforeMinutes), false, status, 0, T.At(0));

    [Fact]
    public void TheSameEventRuleAndChannel_IsStoredOnce()
    {
        Assert.True(_store.TryAdd(Item()));
        Assert.False(_store.TryAdd(Item()));
        Assert.True(_store.TryAdd(Item(channel: "overlay")));
        Assert.True(_store.TryAdd(Item(key: "e2")));
    }

    [Fact]
    public void Due_ReturnsOnlyPendingItemsWhoseTimeHasCome_OldestFirst()
    {
        _store.TryAdd(Item("a"));
        _store.TryAdd(Item("b", notBeforeMinutes: 30));
        _store.TryAdd(Item("c", status: OutboxStatus.Suppressed));
        _store.TryAdd(Item("d"));

        var due = _store.Due(T.At(10), 10);

        Assert.Equal(["a", "d"], due.Select(i => i.EventKey).ToArray());
        Assert.Equal(new Uri("https://x/y"), due[0].Notification.Url);
        Assert.Equal(3, _store.Due(T.At(60), 10).Count);
    }

    [Fact]
    public void ASentItem_LeavesTheQueue()
    {
        _store.TryAdd(Item());

        _store.MarkSent(_store.Due(T.At(0), 1)[0].Id, T.At(1));

        Assert.Empty(_store.Due(T.At(60), 10));
        Assert.Equal(1, _store.Counts()[OutboxStatus.Sent]);
    }

    [Fact]
    public void AFailedItem_ComesBackLater_UntilItIsGivenUp()
    {
        _store.TryAdd(Item());
        var id = _store.Due(T.At(0), 1)[0].Id;

        _store.MarkFailed(id, "timeout", T.At(5), dead: false);

        Assert.Empty(_store.Due(T.At(1), 10));
        var retry = Assert.Single(_store.Due(T.At(5), 10));
        Assert.Equal(1, retry.Attempts);

        _store.MarkFailed(id, "timeout", T.At(9), dead: true);

        Assert.Empty(_store.Due(T.At(600), 10));
        Assert.Equal(1, _store.Counts()[OutboxStatus.Dead]);
    }

    [Fact]
    public void Acknowledgement_IsRememberedPerEvent()
    {
        _store.TryRegisterEvent(new TicketEvent("e1", TicketEventType.TicketCreated, T.At(0)));

        Assert.False(_store.IsEventAcknowledged("e1"));
        _store.AcknowledgeEvent("e1", T.At(2));

        Assert.True(_store.IsEventAcknowledged("e1"));
        Assert.False(_store.IsEventAcknowledged("unknown"));
    }
}
