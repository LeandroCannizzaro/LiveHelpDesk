namespace LiveHelpDesk.Core;

/// <summary>
/// What the option-set values of an installation mean. The defaults describe the QGS help desk; in
/// production this is loaded from <c>dynamics-mapping.json</c> so the detector holds no hard-coded codes.
/// </summary>
public sealed record TicketMapping
{
    public static TicketMapping Qgs { get; } = new();

    public int ActiveState { get; init; } = 0;

    public int ResolvedState { get; init; } = 1;

    public int CancelledState { get; init; } = 2;

    /// <summary>Resolved-state statuses that mean "closed" (the others are "resolved").</summary>
    public IReadOnlySet<int> ClosedStatuses { get; init; } = new HashSet<int> { 5 };

    /// <summary>Cancelled-state statuses that mean "merged" (the others are "cancelled").</summary>
    public IReadOnlySet<int> MergedStatuses { get; init; } = new HashSet<int> { 2000 };

    /// <summary>Support level of each active status: 0 = L0 qualification, 1 = L1, 2 = L2.</summary>
    public IReadOnlyDictionary<int, int> StatusLevels { get; init; } = new Dictionary<int, int>
    {
        [1] = 0,
        [717600002] = 1,
        [717600003] = 1,
        [717600007] = 1,
        [2] = 2,
        [717600006] = 2,
    };

    public int CommunicationReplyReceived { get; init; } = 717600002;

    public int CommunicationReplySent { get; init; } = 717600001;
}
