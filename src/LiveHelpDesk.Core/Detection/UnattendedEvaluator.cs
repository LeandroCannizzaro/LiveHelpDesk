using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.State;

namespace LiveHelpDesk.Core.Detection;

/// <summary>
/// Time-driven check, not a Dynamics event: "the customer wrote (or the ticket was opened) and nobody
/// answered for too long". Only activity observed since the baseline counts, otherwise the hundreds of
/// tickets that already existed would all look unattended at the first run.
/// </summary>
public static class UnattendedEvaluator
{
    private static readonly TimeSpan HistoryTolerance = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<TicketEvent> Evaluate(
        ITicketStateStore store, TicketMapping mapping, TimeSpan threshold, DateTimeOffset now)
    {
        var baselineAt = store.BaselineAt;
        if (baselineAt is null)
        {
            return [];
        }

        var events = new List<TicketEvent>();
        store.InTransaction(() =>
        {
            foreach (var ticket in store.TicketsInState(mapping.ActiveState))
            {
                if (ticket.OnHold)
                {
                    continue;
                }

                var activity = store.GetActivity(ticket.Id);
                var attentionSince = AttentionSince(ticket, activity, baselineAt.Value);
                if (attentionSince is null
                    || now - attentionSince < threshold
                    || activity.LastOutboundAt >= attentionSince)
                {
                    continue;
                }

                var evt = new TicketEvent(
                    DedupKeys.Unattended(ticket.Id, attentionSince.Value),
                    TicketEventType.Unattended,
                    now,
                    ticket.Id,
                    ticket.Number,
                    ticket.Title,
                    Change: $"waiting {(int)(now - attentionSince.Value).TotalMinutes} min",
                    Ticket: ticket);
                if (store.TryRegisterEvent(evt))
                {
                    events.Add(evt);
                }
            }
        });

        return events;
    }

    private static DateTimeOffset? AttentionSince(TicketSnapshot ticket, TicketActivity activity, DateTimeOffset baselineAt)
    {
        DateTimeOffset? openedSinceBaseline = ticket.CreatedOn >= baselineAt - HistoryTolerance ? ticket.CreatedOn : null;
        if (openedSinceBaseline is null)
        {
            return activity.LastInboundAt;
        }

        return activity.LastInboundAt is { } inbound && inbound > openedSinceBaseline ? inbound : openedSinceBaseline;
    }
}
