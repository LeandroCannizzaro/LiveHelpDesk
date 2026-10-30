using System.Globalization;

namespace LiveHelpDesk.Core;

/// <summary>
/// Builders for event dedup keys. A key must be a pure function of the fact, so that re-reading the same
/// data (watermark overlap, restart) never produces a second notification.
/// </summary>
public static class DedupKeys
{
    public static string Created(Guid ticketId) => $"{ticketId:D}:Created";

    public static string Message(Guid messageId) => $"{messageId:D}:MessageReceived";

    public static string StatusChanged(Guid ticketId, int from, int to, DateTimeOffset modifiedOn) =>
        $"{ticketId:D}:StatusChanged:{from}->{to}:{modifiedOn.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";
}
