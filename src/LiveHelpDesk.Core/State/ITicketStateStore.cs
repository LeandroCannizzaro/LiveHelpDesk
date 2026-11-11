using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.State;

/// <summary>When the customer last wrote and when we last answered, as observed since the baseline.</summary>
public sealed record TicketActivity(DateTimeOffset? LastInboundAt, DateTimeOffset? LastOutboundAt);

/// <summary>Everything the detector remembers between polls. One store per user profile.</summary>
public interface ITicketStateStore
{
    /// <summary>When the baseline was taken; null until the first baseline.</summary>
    DateTimeOffset? BaselineAt { get; }

    /// <summary>Runs <paramref name="work"/> atomically: all writes commit together or not at all.</summary>
    void InTransaction(Action work);

    TicketSnapshot? GetTicket(Guid id);

    void SaveTicket(TicketSnapshot ticket);

    void RemoveTicket(Guid id);

    /// <summary>Replaces all remembered tickets with <paramref name="tickets"/> and stamps the baseline time.</summary>
    void SaveBaseline(IEnumerable<TicketSnapshot> tickets, DateTimeOffset at);

    IReadOnlyList<TicketSnapshot> TicketsInState(int state);

    /// <summary>Remembers a message; false when it was already known.</summary>
    bool RecordMessage(MessageSnapshot message);

    TicketActivity GetActivity(Guid ticketId);

    /// <summary>Stores the event unless its dedup key is already known; false means "already raised".</summary>
    bool TryRegisterEvent(TicketEvent evt);

    /// <summary>Marks an event as seen by the user, so scheduled escalations can be dropped.</summary>
    void AcknowledgeEvent(string dedupKey, DateTimeOffset at);

    bool IsEventAcknowledged(string dedupKey);

    /// <summary>Most recent events first.</summary>
    IReadOnlyList<TicketEvent> RecentEvents(int count);

    /// <summary>Events about one ticket that occurred between <paramref name="from"/> and <paramref name="to"/>, oldest first.</summary>
    IReadOnlyList<TicketEvent> EventsForTicket(Guid ticketId, DateTimeOffset from, DateTimeOffset to);

    string? GetValue(string key);

    void SetValue(string key, string value);
}
