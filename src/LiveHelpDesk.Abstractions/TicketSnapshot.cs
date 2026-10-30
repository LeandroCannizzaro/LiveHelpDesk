namespace LiveHelpDesk.Abstractions;

/// <summary>
/// A ticket as read from Dynamics, normalized from the installation's field mapping. Codes are the raw
/// option-set values; their meaning lives in the mapping, not here.
/// </summary>
/// <param name="State">Dataverse state code (active / resolved / cancelled).</param>
/// <param name="Status">Dataverse status code (e.g. L1 assignment, L2 in resolution).</param>
/// <param name="Priority">Priority code, lower is more urgent.</param>
/// <param name="CommunicationStatus">Installation-specific "communications" picklist, when present.</param>
public sealed record TicketSnapshot(
    Guid Id,
    string Number,
    string Title,
    int State,
    int Status,
    int Priority,
    Guid? OwnerId,
    Guid? QueueId,
    Guid? Level2UserId,
    Guid? CustomerId,
    int? CommunicationStatus,
    bool Reminder,
    bool OnHold,
    DateTimeOffset CreatedOn,
    DateTimeOffset ModifiedOn)
{
    /// <summary>Other installation-specific fields that rules may filter on (key = mapping role).</summary>
    public IReadOnlyDictionary<string, string?> Extra { get; init; } = new Dictionary<string, string?>();
}

public enum MessageKind
{
    Email,
    Note,
    Comment,
    Reply,
}

/// <param name="Incoming">True when it comes from the customer, false when it was sent by us.</param>
public sealed record MessageSnapshot(
    Guid Id,
    Guid TicketId,
    MessageKind Kind,
    bool Incoming,
    DateTimeOffset CreatedOn,
    string? From = null,
    string? Subject = null);
