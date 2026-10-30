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
/// <param name="Subtype">Finer classification, e.g. "LevelChanged", "Reopened", "Email".</param>
/// <param name="Change">Transition as "from->to" when the event is a change (status codes, priority...).</param>
/// <param name="Ticket">The ticket as it was when the event was raised (null for health events).</param>
/// <param name="Message">The message that caused the event, for message events.</param>
public sealed record TicketEvent(
    string DedupKey,
    TicketEventType Type,
    DateTimeOffset OccurredAt,
    Guid? TicketId = null,
    string? TicketNumber = null,
    string? Title = null,
    string? Subtype = null,
    string? Change = null,
    TicketSnapshot? Ticket = null,
    MessageSnapshot? Message = null);
