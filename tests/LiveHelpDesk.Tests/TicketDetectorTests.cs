using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Detection;
using LiveHelpDesk.Core.State;
using Xunit;

namespace LiveHelpDesk.Tests;

public sealed class TicketDetectorTests : IDisposable
{
    private readonly SqliteStateStore _store = new(":memory:");
    private readonly TicketDetector _detector;

    public TicketDetectorTests() => _detector = new TicketDetector(_store, TicketMapping.Qgs);

    public void Dispose() => _store.Dispose();

    private void BaselineWith(params TicketSnapshot[] tickets) => _detector.Baseline(tickets, T.Baseline);

    private IReadOnlyList<TicketEvent> Detect(TicketSnapshot ticket) => _detector.Detect([ticket], []).Events;

    [Fact]
    public void Detect_BeforeBaseline_Throws() =>
        Assert.Throws<InvalidOperationException>(() => _detector.Detect([T.Ticket()], []));

    [Fact]
    public void Baseline_RaisesOnlyAHealthEvent_AndRemembersTheTickets()
    {
        var result = _detector.Baseline([T.Ticket(1), T.Ticket(2)], T.Baseline);

        var evt = Assert.Single(result.Events);
        Assert.Equal(TicketEventType.MonitorHealth, evt.Type);
        Assert.Equal("BaselineCompleted", evt.Subtype);
        Assert.NotNull(_store.GetTicket(T.Id(2)));
        Assert.Equal(T.Baseline, _store.BaselineAt);
    }

    [Fact]
    public void RereadingAnUnchangedTicket_RaisesNothing()
    {
        BaselineWith(T.Ticket());

        Assert.Empty(Detect(T.Ticket()));
    }

    [Fact]
    public void NewTicketAfterBaseline_RaisesCreated_ExactlyOnce()
    {
        BaselineWith();
        var fresh = T.Ticket(7, created: T.At(3));

        var first = Detect(fresh);
        var again = Detect(fresh);

        var evt = Assert.Single(first);
        Assert.Equal(TicketEventType.TicketCreated, evt.Type);
        Assert.Equal(fresh.Number, evt.TicketNumber);
        Assert.Equal(fresh, evt.Ticket);
        Assert.Empty(again);
    }

    [Fact]
    public void OldTicketNotInTheBaseline_IsSilentWhenResolved_AndReopenedWhenActive()
    {
        BaselineWith();

        Assert.Empty(Detect(T.Ticket(1, state: 1, status: 5, modified: T.At(1))));

        var reopened = Assert.Single(Detect(T.Ticket(2, state: 0, modified: T.At(2))));
        Assert.Equal(TicketEventType.StatusChanged, reopened.Type);
        Assert.Equal("Reopened", reopened.Subtype);
    }

    [Fact]
    public void OwnerChanges_AreAssignmentThenReassignmentThenUnassignment()
    {
        var ann = T.Id(901);
        var bob = T.Id(902);
        BaselineWith(T.Ticket());

        var assigned = Assert.Single(Detect(T.Ticket(owner: ann, modified: T.At(1))));
        var reassigned = Assert.Single(Detect(T.Ticket(owner: bob, modified: T.At(2))));
        var unassigned = Assert.Single(Detect(T.Ticket(owner: null, modified: T.At(3))));

        Assert.Equal(TicketEventType.TicketAssigned, assigned.Type);
        Assert.Equal(TicketEventType.TicketReassigned, reassigned.Type);
        Assert.Equal(TicketEventType.TicketReassigned, unassigned.Type);
        Assert.Equal("Unassigned", unassigned.Subtype);
    }

    [Theory]
    [InlineData(0, 717600002, 0, 717600003, "StepChanged")] // L1 assignment -> L1 in resolution
    [InlineData(0, 717600003, 0, 2, "LevelChanged")] // L1 -> L2
    [InlineData(0, 717600003, 1, 717600008, "Resolved")]
    [InlineData(0, 717600003, 1, 5, "Closed")]
    [InlineData(0, 717600003, 2, 6, "Cancelled")]
    [InlineData(0, 717600003, 2, 2000, "Merged")]
    [InlineData(1, 717600008, 0, 717600002, "Reopened")]
    public void StatusChanges_AreClassified(int fromState, int fromStatus, int toState, int toStatus, string expected)
    {
        BaselineWith(T.Ticket(state: fromState, status: fromStatus));

        var evt = Assert.Single(Detect(T.Ticket(state: toState, status: toStatus, modified: T.At(1))));

        Assert.Equal(TicketEventType.StatusChanged, evt.Type);
        Assert.Equal(expected, evt.Subtype);
        Assert.Equal($"{fromStatus}->{toStatus}", evt.Change);
    }

    [Fact]
    public void Priority_DistinguishesRaisedFromLowered()
    {
        BaselineWith(T.Ticket(priority: 2));

        var raised = Assert.Single(Detect(T.Ticket(priority: 1, modified: T.At(1))));
        var lowered = Assert.Single(Detect(T.Ticket(priority: 3, modified: T.At(2))));

        Assert.Equal("Increased", raised.Subtype);
        Assert.Equal("Decreased", lowered.Subtype);
    }

    [Fact]
    public void QueueAndLevel2Changes_AreReported()
    {
        BaselineWith(T.Ticket());

        var events = Detect(T.Ticket(queue: T.Id(50), level2: T.Id(60), modified: T.At(1)));

        Assert.Equal(
            [TicketEventType.QueueChanged, TicketEventType.Level2AssigneeChanged],
            events.Select(e => e.Type).ToArray());
    }

    [Fact]
    public void CommunicationStatus_MapsToCustomerRepliedAndReplySent()
    {
        BaselineWith(T.Ticket(comm: 717600000));

        var replied = Assert.Single(Detect(T.Ticket(comm: 717600002, modified: T.At(1))));
        var sent = Assert.Single(Detect(T.Ticket(comm: 717600001, modified: T.At(2))));

        Assert.Equal(TicketEventType.CustomerReplied, replied.Type);
        Assert.Equal(TicketEventType.ReplySent, sent.Type);
    }

    [Fact]
    public void Reminder_RaisesAnEventOnlyWhenItTurnsOn()
    {
        BaselineWith(T.Ticket());

        var on = Assert.Single(Detect(T.Ticket(reminder: true, modified: T.At(1))));
        var stillOn = Detect(T.Ticket(reminder: true, modified: T.At(2)));

        Assert.Equal(TicketEventType.ReminderFlagged, on.Type);
        Assert.Empty(stillOn);
    }

    [Fact]
    public void IncomingAndOutgoingMessages_RaiseDifferentEvents_AndCarryTheMessage()
    {
        BaselineWith(T.Ticket());

        var result = _detector.Detect([], [T.Message(1, 1, incoming: true, atMinutes: 1), T.Message(2, 1, incoming: false, atMinutes: 2)]);

        Assert.Equal([TicketEventType.MessageReceived, TicketEventType.ReplySent], result.Events.Select(e => e.Type).ToArray());
        Assert.Equal("Email", result.Events[0].Subtype);
        Assert.Equal("cliente@example.com", result.Events[0].Message?.From);
        Assert.NotNull(result.Events[0].Ticket);
    }

    [Fact]
    public void AMessage_IsReportedOnlyOnce()
    {
        BaselineWith(T.Ticket());
        var message = T.Message(1, 1);

        Assert.Single(_detector.Detect([], [message]).Events);
        Assert.Empty(_detector.Detect([], [message]).Events);
    }

    [Fact]
    public void AMessageForAnUnknownTicket_IsReturnedUnresolved_AndReportedOnceTheTicketIsKnown()
    {
        BaselineWith();
        var message = T.Message(1, 9);

        var first = _detector.Detect([], [message]);
        Assert.Empty(first.Events);
        Assert.Equal(message, Assert.Single(first.UnresolvedMessages));

        var second = _detector.Detect([T.Ticket(9, modified: T.At(1))], [message]);
        Assert.Contains(second.Events, e => e.Type == TicketEventType.MessageReceived);
        Assert.Empty(second.UnresolvedMessages);
    }

    [Fact]
    public void AMessageFromBeforeTheBaseline_IsRememberedButNotReported()
    {
        BaselineWith(T.Ticket());
        var old = T.Message(1, 1, atMinutes: -60);

        Assert.Empty(_detector.Detect([], [old]).Events);
        Assert.Empty(_detector.Detect([], [old]).Events);
    }

    [Fact]
    public void ANewTicketAndItsFirstMessage_ArriveTogether()
    {
        BaselineWith();
        var fresh = T.Ticket(5, created: T.At(1));

        var result = _detector.Detect([fresh], [T.Message(1, 5, atMinutes: 1)]);

        Assert.Equal(
            [TicketEventType.TicketCreated, TicketEventType.MessageReceived],
            result.Events.Select(e => e.Type).ToArray());
    }

    [Fact]
    public void TheSameChange_IsNeverRaisedTwice_EvenIfTheStoredSnapshotIsStale()
    {
        // The event key is the last line of defence: it must hold even when comparing snapshots would
        // re-detect the change (e.g. an older version of the ticket overwrote the stored one).
        var before = T.Ticket(priority: 2);
        var after = T.Ticket(priority: 1, modified: T.At(1));
        BaselineWith(before);

        Assert.Single(Detect(after));
        _store.SaveTicket(before);

        Assert.Empty(Detect(after));
    }

    [Fact]
    public void NothingIsRaisedTwice_EvenAfterARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lhd-{Guid.NewGuid():N}.db");
        try
        {
            var fresh = T.Ticket(5, created: T.At(1));
            var message = T.Message(1, 5, atMinutes: 1);

            using (var first = new SqliteStateStore(path))
            {
                var detector = new TicketDetector(first, TicketMapping.Qgs);
                detector.Baseline([], T.Baseline);
                Assert.Equal(2, detector.Detect([fresh], [message]).Events.Count);
            }

            using (var second = new SqliteStateStore(path))
            {
                var detector = new TicketDetector(second, TicketMapping.Qgs);
                Assert.Empty(detector.Detect([fresh], [message]).Events);
                Assert.Equal(T.Baseline, second.BaselineAt);
            }
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
