using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.State;

namespace LiveHelpDesk.Core.Detection;

/// <param name="Events">New facts, already deduplicated and persisted.</param>
/// <param name="UnresolvedMessages">Messages whose ticket is not known yet; fetch the ticket and offer them again.</param>
public sealed record DetectionResult(IReadOnlyList<TicketEvent> Events, IReadOnlyList<MessageSnapshot> UnresolvedMessages);

/// <summary>
/// Turns "what Dynamics shows now" into "what changed since last time". It holds no state of its own:
/// everything it remembers lives in the <see cref="ITicketStateStore"/>, so a restart changes nothing.
/// </summary>
public sealed class TicketDetector(ITicketStateStore store, TicketMapping mapping)
{
    /// <summary>Things stamped just before the baseline are treated as history, not as news.</summary>
    private static readonly TimeSpan HistoryTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Remembers the current tickets without raising a single notification about them.</summary>
    public DetectionResult Baseline(IReadOnlyCollection<TicketSnapshot> activeTickets, DateTimeOffset serverNow)
    {
        var events = new List<TicketEvent>();
        store.InTransaction(() =>
        {
            store.SaveBaseline(activeTickets, serverNow);
            var done = new TicketEvent($"baseline:{serverNow.UtcTicks}", TicketEventType.MonitorHealth, serverNow, Subtype: "BaselineCompleted");
            if (store.TryRegisterEvent(done))
            {
                events.Add(done);
            }
        });
        return new DetectionResult(events, []);
    }

    public DetectionResult Detect(IReadOnlyCollection<TicketSnapshot> changedTickets, IReadOnlyCollection<MessageSnapshot> newMessages)
    {
        var baselineAt = store.BaselineAt
            ?? throw new InvalidOperationException("A baseline must be taken before detecting changes.");

        var events = new List<TicketEvent>();
        var unresolved = new List<MessageSnapshot>();

        store.InTransaction(() =>
        {
            var current = new Dictionary<Guid, TicketSnapshot>();

            foreach (var ticket in changedTickets.OrderBy(t => t.ModifiedOn))
            {
                foreach (var evt in Compare(store.GetTicket(ticket.Id), ticket, baselineAt))
                {
                    Emit(evt);
                }

                store.SaveTicket(ticket);
                current[ticket.Id] = ticket;
            }

            foreach (var message in newMessages.OrderBy(m => m.CreatedOn))
            {
                var ticket = current.GetValueOrDefault(message.TicketId) ?? store.GetTicket(message.TicketId);
                if (ticket is null)
                {
                    unresolved.Add(message);
                    continue;
                }

                if (!store.RecordMessage(message) || message.CreatedOn < baselineAt - HistoryTolerance)
                {
                    continue;
                }

                Emit(MessageEvent(message, ticket));
            }
        });

        return new DetectionResult(events, unresolved);

        void Emit(TicketEvent evt)
        {
            if (store.TryRegisterEvent(evt))
            {
                events.Add(evt);
            }
        }
    }

    /// <summary>Remembers tickets seen for the first time, silently. Used to resolve messages whose ticket was unknown.</summary>
    public void Remember(IEnumerable<TicketSnapshot> tickets) =>
        store.InTransaction(() =>
        {
            foreach (var ticket in tickets)
            {
                if (store.GetTicket(ticket.Id) is null)
                {
                    store.SaveTicket(ticket);
                }
            }
        });

    /// <summary>Forgets a ticket that is no longer readable (merged away, deleted, access lost) and says so once.</summary>
    public TicketEvent? MarkDisappeared(Guid ticketId, DateTimeOffset now)
    {
        TicketEvent? raised = null;
        store.InTransaction(() =>
        {
            var previous = store.GetTicket(ticketId);
            if (previous is null)
            {
                return;
            }

            store.RemoveTicket(ticketId);
            var evt = new TicketEvent(
                $"{ticketId:D}:Disappeared", TicketEventType.StatusChanged, now, ticketId, previous.Number, previous.Title,
                "Disappeared", Ticket: previous);
            if (store.TryRegisterEvent(evt))
            {
                raised = evt;
            }
        });
        return raised;
    }

    private static TicketEvent MessageEvent(MessageSnapshot message, TicketSnapshot ticket) =>
        new(
            DedupKeys.Message(message.Id, message.Incoming),
            message.Incoming ? TicketEventType.MessageReceived : TicketEventType.ReplySent,
            message.CreatedOn,
            ticket.Id,
            ticket.Number,
            ticket.Title,
            Subtype: message.Kind.ToString(),
            Ticket: ticket,
            Message: message);

    private IEnumerable<TicketEvent> Compare(TicketSnapshot? previous, TicketSnapshot ticket, DateTimeOffset baselineAt)
    {
        if (previous is null)
        {
            // Never seen: either really new, or an old ticket that came back to life (it was not in the baseline).
            if (ticket.CreatedOn >= baselineAt - HistoryTolerance)
            {
                yield return For(
                    ticket, DedupKeys.Created(ticket.Id), TicketEventType.TicketCreated, ticket.CreatedOn);
            }
            else if (ticket.State == mapping.ActiveState)
            {
                yield return For(
                    ticket,
                    DedupKeys.StatusChanged(ticket.Id, -1, ticket.Status, ticket.ModifiedOn),
                    TicketEventType.StatusChanged,
                    ticket.ModifiedOn,
                    "Reopened",
                    $"-1->{ticket.Status}");
            }

            yield break;
        }

        if (previous.OwnerId != ticket.OwnerId)
        {
            yield return Changed(
                previous, ticket, "Owner",
                previous.OwnerId is null ? TicketEventType.TicketAssigned : TicketEventType.TicketReassigned,
                previous.OwnerId?.ToString("D"),
                ticket.OwnerId?.ToString("D"),
                subtype: ticket.OwnerId is null ? "Unassigned" : null);
        }

        if (previous.QueueId != ticket.QueueId)
        {
            yield return Changed(previous, ticket, "Queue", TicketEventType.QueueChanged, previous.QueueId?.ToString("D"), ticket.QueueId?.ToString("D"));
        }

        if (previous.Level2UserId != ticket.Level2UserId)
        {
            yield return Changed(previous, ticket, "Level2", TicketEventType.Level2AssigneeChanged, previous.Level2UserId?.ToString("D"), ticket.Level2UserId?.ToString("D"));
        }

        if (previous.Status != ticket.Status || previous.State != ticket.State)
        {
            yield return For(
                ticket,
                DedupKeys.StatusChanged(ticket.Id, previous.Status, ticket.Status, ticket.ModifiedOn),
                TicketEventType.StatusChanged,
                ticket.ModifiedOn,
                StatusSubtype(previous, ticket),
                $"{previous.Status}->{ticket.Status}");
        }

        if (previous.Priority != ticket.Priority)
        {
            // Dynamics priorities count down: 1 is the most urgent.
            yield return Changed(
                previous, ticket, "Priority", TicketEventType.PriorityChanged,
                previous.Priority.ToString(), ticket.Priority.ToString(),
                subtype: ticket.Priority < previous.Priority ? "Increased" : "Decreased");
        }

        if (previous.CommunicationStatus != ticket.CommunicationStatus && ticket.CommunicationStatus is { } comm)
        {
            if (comm == mapping.CommunicationReplyReceived)
            {
                yield return Changed(previous, ticket, "Comm", TicketEventType.CustomerReplied, previous.CommunicationStatus?.ToString(), comm.ToString());
            }
            else if (comm == mapping.CommunicationReplySent)
            {
                yield return Changed(previous, ticket, "Comm", TicketEventType.ReplySent, previous.CommunicationStatus?.ToString(), comm.ToString(), subtype: "CommunicationStatus");
            }
        }

        if (!previous.Reminder && ticket.Reminder)
        {
            yield return Changed(previous, ticket, "Reminder", TicketEventType.ReminderFlagged, "false", "true");
        }
    }

    private string StatusSubtype(TicketSnapshot previous, TicketSnapshot ticket)
    {
        if (previous.State != ticket.State)
        {
            if (ticket.State == mapping.ActiveState)
            {
                return "Reopened";
            }

            if (ticket.State == mapping.ResolvedState)
            {
                return mapping.ClosedStatuses.Contains(ticket.Status) ? "Closed" : "Resolved";
            }

            return mapping.MergedStatuses.Contains(ticket.Status) ? "Merged" : "Cancelled";
        }

        var before = mapping.StatusLevels.GetValueOrDefault(previous.Status, -1);
        var after = mapping.StatusLevels.GetValueOrDefault(ticket.Status, -1);
        return before != after ? "LevelChanged" : "StepChanged";
    }

    private static TicketEvent Changed(
        TicketSnapshot previous, TicketSnapshot ticket, string kind, TicketEventType type, string? from, string? to, string? subtype = null) =>
        For(
            ticket,
            DedupKeys.Change(ticket.Id, kind, from ?? "none", to ?? "none", ticket.ModifiedOn),
            type,
            ticket.ModifiedOn,
            subtype,
            $"{from ?? "none"}->{to ?? "none"}");

    private static TicketEvent For(
        TicketSnapshot ticket, string key, TicketEventType type, DateTimeOffset at, string? subtype = null, string? change = null) =>
        new(key, type, at, ticket.Id, ticket.Number, ticket.Title, subtype, change, ticket);
}
