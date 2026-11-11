using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Detection;
using LiveHelpDesk.Core.State;
using Xunit;

namespace LiveHelpDesk.Tests;

public sealed class SqliteStateStoreTests : IDisposable
{
    private readonly SqliteStateStore _store = new(":memory:");

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ANewDatabase_IsMigratedToTheLatestSchema() =>
        Assert.Equal(3, _store.SchemaVersion);

    [Fact]
    public void ATicket_RoundTripsWithItsExtraFields()
    {
        var ticket = T.Ticket(3, extra: new Dictionary<string, string?> { ["applicativo"] = "BFA" }) with
        {
            Labels = new Dictionary<string, string?> { [TicketLabels.Customer] = "Rossi S.r.l." },
        };

        _store.SaveTicket(ticket);
        var loaded = _store.GetTicket(ticket.Id);

        Assert.NotNull(loaded);
        Assert.Equal(ticket with { }, loaded with { Extra = ticket.Extra, Labels = ticket.Labels });
        Assert.Equal("BFA", loaded.Extra["applicativo"]);
        Assert.Equal("Rossi S.r.l.", loaded.Labels[TicketLabels.Customer]);
    }

    [Fact]
    public void SaveBaseline_ReplacesWhatWasKnownBefore()
    {
        _store.SaveTicket(T.Ticket(1));

        _store.SaveBaseline([T.Ticket(2)], T.Baseline);

        Assert.Null(_store.GetTicket(T.Id(1)));
        Assert.NotNull(_store.GetTicket(T.Id(2)));
    }

    [Fact]
    public void TicketsInState_FiltersByState()
    {
        _store.SaveTicket(T.Ticket(1, state: 0));
        _store.SaveTicket(T.Ticket(2, state: 1));

        Assert.Equal(T.Id(1), Assert.Single(_store.TicketsInState(0)).Id);
    }

    [Fact]
    public void Messages_TrackTheLatestInboundAndOutboundTimes()
    {
        _store.SaveTicket(T.Ticket(1));

        Assert.True(_store.RecordMessage(T.Message(1, 1, incoming: true, atMinutes: 5)));
        Assert.True(_store.RecordMessage(T.Message(2, 1, incoming: true, atMinutes: 2)));
        Assert.True(_store.RecordMessage(T.Message(3, 1, incoming: false, atMinutes: 7)));
        Assert.False(_store.RecordMessage(T.Message(1, 1, incoming: true, atMinutes: 5)));

        var activity = _store.GetActivity(T.Id(1));
        Assert.Equal(T.At(5), activity.LastInboundAt);
        Assert.Equal(T.At(7), activity.LastOutboundAt);
    }

    [Fact]
    public void ActivityOfAnUnknownTicket_IsEmpty() =>
        Assert.Equal(new TicketActivity(null, null), _store.GetActivity(T.Id(404)));

    [Fact]
    public void AnEventWithTheSameKey_IsRegisteredOnce()
    {
        var evt = new TicketEvent("k1", TicketEventType.TicketCreated, T.At(1), T.Id(1), "1", "t");

        Assert.True(_store.TryRegisterEvent(evt));
        Assert.False(_store.TryRegisterEvent(evt with { Title = "changed" }));
    }

    [Fact]
    public void RecentEvents_AreNewestFirst_AndKeepTheirPayload()
    {
        var ticket = T.Ticket(1);
        _store.TryRegisterEvent(new TicketEvent("a", TicketEventType.TicketCreated, T.At(1), Ticket: ticket));
        _store.TryRegisterEvent(new TicketEvent("b", TicketEventType.StatusChanged, T.At(2), Subtype: "Closed"));

        var events = _store.RecentEvents(10);

        Assert.Equal(["b", "a"], events.Select(e => e.DedupKey).ToArray());
        Assert.Equal(ticket.Number, events[1].Ticket?.Number);
        Assert.Single(_store.RecentEvents(1));
    }

    [Fact]
    public void EventsForTicket_KeepsOnlyThatTicketInsideTheInterval()
    {
        _store.TryRegisterEvent(new TicketEvent("early", TicketEventType.TicketCreated, T.At(1), T.Id(1)));
        _store.TryRegisterEvent(new TicketEvent("in", TicketEventType.StatusChanged, T.At(5), T.Id(1)));
        _store.TryRegisterEvent(new TicketEvent("other", TicketEventType.StatusChanged, T.At(5), T.Id(2)));
        _store.TryRegisterEvent(new TicketEvent("edge", TicketEventType.PriorityChanged, T.At(10), T.Id(1)));

        var events = _store.EventsForTicket(T.Id(1), T.At(5), T.At(10));

        Assert.Equal(["in", "edge"], events.Select(e => e.DedupKey).ToArray());
    }

    [Fact]
    public void AFailedTransaction_LeavesNothingBehind()
    {
        Assert.Throws<InvalidOperationException>(() => _store.InTransaction(() =>
        {
            _store.SaveTicket(T.Ticket(1));
            throw new InvalidOperationException("boom");
        }));

        Assert.Null(_store.GetTicket(T.Id(1)));
    }

    [Fact]
    public void Values_ArePersistedAndOverwritten()
    {
        Assert.Null(_store.GetValue("watermark.email"));

        _store.SetValue("watermark.email", "a");
        _store.SetValue("watermark.email", "b");

        Assert.Equal("b", _store.GetValue("watermark.email"));
    }

    [Fact]
    public void ReopeningTheFile_KeepsTheDataAndDoesNotMigrateAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lhd-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new SqliteStateStore(path))
            {
                first.SaveBaseline([T.Ticket(1)], T.Baseline);
            }

            using var second = new SqliteStateStore(path);
            Assert.Equal(3, second.SchemaVersion);
            Assert.NotNull(second.GetTicket(T.Id(1)));
            Assert.Equal(T.Baseline, second.BaselineAt);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(path)}*"))
            {
                File.Delete(file);
            }
        }
    }
}

public sealed class UnattendedEvaluatorTests : IDisposable
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    private readonly SqliteStateStore _store = new(":memory:");
    private readonly TicketDetector _detector;

    public UnattendedEvaluatorTests() => _detector = new TicketDetector(_store, TicketMapping.Qgs);

    public void Dispose() => _store.Dispose();

    private IReadOnlyList<TicketEvent> Evaluate(int atMinutes) =>
        UnattendedEvaluator.Evaluate(_store, TicketMapping.Qgs, Threshold, T.At(atMinutes));

    [Fact]
    public void TicketsThatAlreadyExistedAtTheBaseline_AreNeverFlaggedForOldSilence()
    {
        _detector.Baseline([.. Enumerable.Range(1, 50).Select(i => T.Ticket(i))], T.Baseline);

        Assert.Empty(Evaluate(60 * 24 * 7));
    }

    [Fact]
    public void ANewTicketNobodyAnswers_IsFlaggedOnce_AfterTheThreshold()
    {
        _detector.Baseline([], T.Baseline);
        _detector.Detect([T.Ticket(1, created: T.At(1))], []);

        Assert.Empty(Evaluate(20));
        var flagged = Assert.Single(Evaluate(40));
        Assert.Equal(TicketEventType.Unattended, flagged.Type);
        Assert.Empty(Evaluate(90));
    }

    [Fact]
    public void AnAnswer_StopsTheClock()
    {
        _detector.Baseline([], T.Baseline);
        _detector.Detect([T.Ticket(1, created: T.At(1))], [T.Message(1, 1, incoming: false, atMinutes: 10)]);

        Assert.Empty(Evaluate(120));
    }

    [Fact]
    public void ANewCustomerMessageAfterAnAnswer_StartsANewEpisode()
    {
        _detector.Baseline([], T.Baseline);
        _detector.Detect([T.Ticket(1, created: T.At(1))], [T.Message(1, 1, incoming: false, atMinutes: 10)]);
        _detector.Detect([], [T.Message(2, 1, incoming: true, atMinutes: 100)]);

        Assert.Empty(Evaluate(110));
        Assert.Single(Evaluate(140));
    }

    [Fact]
    public void TicketsOnHold_AreIgnored()
    {
        _detector.Baseline([], T.Baseline);
        _detector.Detect([T.Ticket(1, created: T.At(1), onHold: true)], []);

        Assert.Empty(Evaluate(300));
    }

    [Fact]
    public void WithoutABaseline_NothingIsEvaluated() =>
        Assert.Empty(Evaluate(300));
}
