using System.Globalization;

namespace LiveHelpDesk.Core;

/// <summary>
/// Builders for event dedup keys. A key must be a pure function of the fact, so that re-reading the same
/// data (watermark overlap, restart) never produces a second notification.
/// </summary>
public static class DedupKeys
{
    public static string Created(Guid ticketId) => $"{ticketId:D}:Created";

    public static string Message(Guid messageId, bool incoming) =>
        $"{messageId:D}:{(incoming ? "MessageReceived" : "ReplySent")}";

    public static string StatusChanged(Guid ticketId, int from, int to, DateTimeOffset modifiedOn) =>
        Change(ticketId, "StatusChanged", from.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture), modifiedOn);

    /// <summary>Generic transition key: the same ticket moving between the same two values at the same time.</summary>
    public static string Change(Guid ticketId, string kind, string from, string to, DateTimeOffset at) =>
        $"{ticketId:D}:{kind}:{from}->{to}:{at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";

    /// <summary>One "unattended" episode: it starts when the customer last wrote and ends with a reply.</summary>
    public static string Unattended(Guid ticketId, DateTimeOffset attentionSince) =>
        $"{ticketId:D}:Unattended:{attentionSince.UtcTicks}";
}
