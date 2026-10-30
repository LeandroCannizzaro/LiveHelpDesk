using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Sources;

public enum SourceFailure
{
    /// <summary>The user must sign in again (no token, or the refresh token is no longer valid).</summary>
    AuthRequired,

    /// <summary>The account is not allowed to read what was asked.</summary>
    Forbidden,

    /// <summary>Too many requests; honour <see cref="TicketSourceException.RetryAfter"/>.</summary>
    Throttled,

    /// <summary>Network trouble or a server-side outage: retrying later may work.</summary>
    Unavailable,

    /// <summary>The server rejected the request itself (bad query, unknown column): a bug, not an outage.</summary>
    Error,
}

public sealed class TicketSourceException(SourceFailure failure, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public SourceFailure Failure { get; } = failure;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Where tickets come from. Dynamics is the only implementation; tests use a fake.</summary>
public interface ITicketSource
{
    /// <summary>The server's clock: watermarks must never depend on this machine's clock.</summary>
    Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TicketSnapshot>> GetActiveTicketsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> GetActiveTicketIdsAsync(CancellationToken cancellationToken);

    /// <summary>Tickets in any state modified at or after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<TicketSnapshot>> GetTicketsModifiedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>The tickets that exist and are readable among <paramref name="ids"/>.</summary>
    Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>E-mails, notes and the other enabled sources created at or after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<MessageSnapshot>> GetMessagesCreatedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);
}
