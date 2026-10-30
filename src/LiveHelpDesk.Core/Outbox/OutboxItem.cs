using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Outbox;

public enum OutboxStatus
{
    Pending,
    Sent,

    /// <summary>Gave up after too many attempts.</summary>
    Dead,

    /// <summary>Not sent on purpose (quiet hours); kept for the record.</summary>
    Suppressed,

    /// <summary>No longer needed: already acknowledged, expired, or the channel is not configured.</summary>
    Cancelled,
}

/// <param name="Id">0 until stored.</param>
/// <param name="NotBefore">Escalations are scheduled in the future.</param>
/// <param name="RequiresUnacknowledged">Drop it if the event was acknowledged before <paramref name="NotBefore"/> (escalations).</param>
public sealed record OutboxItem(
    long Id,
    string EventKey,
    string RuleId,
    string Channel,
    Notification Notification,
    DateTimeOffset NotBefore,
    bool RequiresUnacknowledged,
    OutboxStatus Status,
    int Attempts,
    DateTimeOffset CreatedAt);

/// <summary>Notifications waiting to leave. One row per (event, rule, channel): re-planning the same fact adds nothing.</summary>
public interface IOutbox
{
    /// <summary>False when the same (event, rule, channel) was already planned.</summary>
    bool TryAdd(OutboxItem item);

    /// <summary>Pending items whose time has come, oldest first.</summary>
    IReadOnlyList<OutboxItem> Due(DateTimeOffset now, int max);

    /// <summary>Like <see cref="Due"/> but leaving out some channels: the app's own channels are collected by the app,
    /// and must never crowd out the ones the service sends itself.</summary>
    IReadOnlyList<OutboxItem> DueExcluding(IReadOnlyCollection<string> excludedChannels, DateTimeOffset now, int max);

    void MarkSent(long id, DateTimeOffset at);

    /// <summary>The item will never be sent, for the stated reason.</summary>
    void MarkCancelled(long id, string reason, DateTimeOffset at);

    /// <summary>Records a failed attempt; with <paramref name="dead"/> it will never be retried.</summary>
    void MarkFailed(long id, string error, DateTimeOffset nextAttempt, bool dead);

    IReadOnlyDictionary<OutboxStatus, int> Counts();

    IReadOnlyDictionary<string, int> PendingByChannel();
}
