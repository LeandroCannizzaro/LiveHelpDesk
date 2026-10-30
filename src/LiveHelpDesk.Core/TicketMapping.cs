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

    /// <summary>Human names of the status codes, used in notification text.</summary>
    public IReadOnlyDictionary<int, string> StatusLabels { get; init; } = new Dictionary<int, string>
    {
        [1] = "L0 Qualifica",
        [717600002] = "L1 Assegnazione",
        [717600003] = "L1 In risoluzione",
        [2] = "L2 In risoluzione",
        [717600006] = "L2 Risolto",
        [717600007] = "L1 Risolto",
        [5] = "Chiuso",
        [717600008] = "Risolto",
        [6] = "Annullato",
        [2000] = "Unito",
    };

    public IReadOnlyDictionary<int, string> PriorityLabels { get; init; } = new Dictionary<int, string>
    {
        [1] = "Alta",
        [2] = "Normale",
        [3] = "Bassa",
    };

    public int CommunicationReplyReceived { get; init; } = 717600002;

    public int CommunicationReplySent { get; init; } = 717600001;
}
