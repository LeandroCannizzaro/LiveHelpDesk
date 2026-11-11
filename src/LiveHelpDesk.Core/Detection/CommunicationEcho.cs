using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.State;

namespace LiveHelpDesk.Core.Detection;

/// <summary>
/// The ticket's "communications" field changes because an e-mail was received or sent: the same fact the
/// detector already reports through the e-mail itself. Finds that e-mail, so a rule listening to both
/// signals notifies once.
/// </summary>
public static class CommunicationEcho
{
    /// <summary>Subtype of the <see cref="TicketEventType.ReplySent"/> raised by the communications field.</summary>
    public const string Subtype = "CommunicationStatus";

    /// <summary>The e-mail can be created well before the field changes (a draft, a slow workflow).</summary>
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(30);

    /// <summary>The ticket stamp and the e-mail stamp can disagree by a little either way.</summary>
    private static readonly TimeSpan After = TimeSpan.FromMinutes(5);

    /// <summary>The e-mail events that explain <paramref name="evt"/>; empty when it is not an echo or nothing explains it.</summary>
    public static IReadOnlyList<TicketEvent> CausesOf(TicketEvent evt, ITicketStateStore store)
    {
        if (evt.TicketId is not { } ticketId || IncomingEcho(evt) is not { } incoming)
        {
            return [];
        }

        return store.EventsForTicket(ticketId, evt.OccurredAt - Before, evt.OccurredAt + After)
            .Where(e => e.DedupKey != evt.DedupKey && e.Message is { Kind: MessageKind.Email } m && m.Incoming == incoming)
            .ToList();
    }

    /// <summary>True for an echo of a received e-mail, false for one of a sent e-mail, null when not an echo.</summary>
    private static bool? IncomingEcho(TicketEvent evt) => evt switch
    {
        { Type: TicketEventType.CustomerReplied, Message: null } => true,
        { Type: TicketEventType.ReplySent, Message: null, Subtype: Subtype } => false,
        _ => null,
    };
}
