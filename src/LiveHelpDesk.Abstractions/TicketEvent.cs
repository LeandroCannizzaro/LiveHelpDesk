namespace LiveHelpDesk.Abstractions;

/// <summary>Kinds of facts the detector can raise (see the event model in the implementation plan).</summary>
public enum TicketEventType
{
    TicketCreated,
    MessageReceived,
    CustomerReplied,
    ReplySent,
    TicketAssigned,
    TicketReassigned,
    QueueChanged,
    Level2AssigneeChanged,
    StatusChanged,
    PriorityChanged,
    ReminderFlagged,
    SlaWarning,
    SlaBreached,
    Unattended,
    MonitorHealth,
}

public enum Severity
{
    Info,
    Attention,
    Critical,
}

/// <summary>A normalized fact about a ticket, ready for the rule engine.</summary>
/// <param name="DedupKey">Stable key: the same fact must always produce the same key.</param>
/// <param name="Subtype">Finer classification, e.g. "LevelChanged" or "Email".</param>
public sealed record TicketEvent(
    string DedupKey,
    TicketEventType Type,
    DateTimeOffset OccurredAt,
    Guid? TicketId = null,
    string? TicketNumber = null,
    string? Title = null,
    string? Subtype = null);
