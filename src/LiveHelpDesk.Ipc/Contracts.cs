using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Ipc;

public static class IpcProtocol
{
    /// <summary>Bumped when a call changes in a way an older app or service cannot understand.</summary>
    public const int Version = 1;

    public const string PipeName = "LiveHelpDesk";
}

public enum ProfileState
{
    /// <summary>No usable sign-in: the user has to log in (first time, or the login expired).</summary>
    NeedsLogin,

    /// <summary>A device-code login is waiting for the user.</summary>
    LoggingIn,

    /// <summary>Signed in, taking the baseline.</summary>
    Starting,

    Running,

    /// <summary>Signed in but the last polls failed (network, throttling, a bad query...).</summary>
    Degraded,
}

public enum ChangeKind
{
    Status,
    Notifications,
    Events,
}

public sealed class ChangedEventArgs(ChangeKind kind) : EventArgs
{
    public ChangeKind Kind { get; } = kind;
}

/// <param name="UserName">The signed-in Dynamics user, once known.</param>
/// <param name="HomeUrl">The help desk dashboard in Dynamics: where the app's browser starts.</param>
public sealed record ServiceStatus(
    int ProtocolVersion,
    string ServiceVersion,
    ProfileState State,
    string? UserName,
    DateTimeOffset? BaselineAt,
    DateTimeOffset? LastPollAt,
    string? LastError,
    int ActiveTickets,
    int PendingNotifications,
    Uri? HomeUrl = null);

/// <summary>What the user must do to sign in: open the address and type the code.</summary>
public sealed record LoginChallenge(string UserCode, string VerificationUrl, DateTimeOffset ExpiresAt, string Message);

public sealed record EventInfo(
    string Key,
    string Type,
    string? Subtype,
    string? TicketNumber,
    string? Title,
    DateTimeOffset OccurredAt,
    bool Acknowledged,
    Uri? Url = null);

public sealed record NotificationInfo(
    long Id,
    string EventKey,
    string Channel,
    string Title,
    string Body,
    Severity Severity,
    Uri? Url);

/// <summary>How one outside channel is doing.</summary>
/// <param name="State">"Closed" (working), "Open" (failing, paused for a while) or "HalfOpen" (trying again).</param>
public sealed record ChannelInfo(
    string Id,
    bool Enabled,
    string State,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure,
    string? LastError,
    int SentLastHour,
    int Pending);

public sealed record ChannelTestResult(bool Ok, string? Error);

/// <summary>The Microsoft 365 account the e-mail channel sends as.</summary>
/// <param name="Account">The account's address; null when nobody is signed in.</param>
/// <param name="LoginPending">A device-code sign-in is waiting for the user.</param>
/// <param name="LastError">Why the last sign-in attempt did not complete.</param>
public sealed record MailAccountInfo(string? Account, bool LoginPending, string? LastError);

/// <summary>The outcome of saving the rules or the notification texts.</summary>
/// <param name="Errors">What is wrong, in plain words, when <paramref name="Saved"/> is false.</param>
public sealed record RulesSaveResult(bool Saved, IReadOnlyList<string> Errors);

/// <summary>A name behind an id the rules can filter on (a queue, a team, a user).</summary>
/// <param name="Kind">"queue", "team" or "user".</param>
public sealed record LookupItem(string Id, string Name, string Kind);

public sealed record RuleHitCount(string RuleId, string RuleName, int Matches);

public sealed record DryRunTicket(string TicketNumber, string? Title, string RuleId);

/// <summary>What the rules would do with today's tickets if each had just been created.</summary>
public sealed record DryRunResult(int TicketsEvaluated, IReadOnlyList<RuleHitCount> Rules, IReadOnlyList<DryRunTicket> Examples);

/// <summary>
/// What the desktop app can ask of the service, always about the caller's own profile (the service tells
/// callers apart by their Windows identity, so there is no way to name someone else's).
/// </summary>
public interface ILiveHelpDeskApi
{
    Task<ServiceStatus> GetStatusAsync();

    /// <summary>Starts a device-code sign-in and returns as soon as the code is known; progress shows in the status.</summary>
    Task<LoginChallenge> StartLoginAsync();

    /// <summary>Most recent first.</summary>
    Task<IReadOnlyList<EventInfo>> GetRecentEventsAsync(int count);

    /// <summary>The user has seen this event: scheduled escalations for it are dropped.</summary>
    Task AcknowledgeAsync(string eventKey);

    /// <summary>
    /// Hands over the queued notifications for the given channels (e.g. toast, overlay) and removes them from the
    /// queue. Meant for the app, which shows them on screen; external channels are sent by the service itself.
    /// </summary>
    Task<IReadOnlyList<NotificationInfo>> ClaimNotificationsAsync(IReadOnlyList<string> channels);

    /// <summary>Tries the rules on the active tickets (read only), as if each had just been created.</summary>
    Task<DryRunResult> DryRunRulesAsync();

    /// <summary>The outside channels' settings. Secrets (passwords, tokens, webhook addresses) come back masked.</summary>
    Task<ChannelSettings> GetChannelSettingsAsync();

    /// <summary>Replaces the settings. A secret sent back as the mask is kept as it was. Throws if something is invalid.</summary>
    Task SaveChannelSettingsAsync(ChannelSettings settings);

    Task<IReadOnlyList<ChannelInfo>> GetChannelStatusAsync();

    /// <summary>Sends a harmless test message through one channel (email, teams, sms, whatsapp).</summary>
    Task<ChannelTestResult> TestChannelAsync(string channelId);

    /// <summary>Starts the device-code sign-in to Outlook for the e-mail channel; returns as soon as the code is known.</summary>
    Task<LoginChallenge> StartMailLoginAsync();

    Task<MailAccountInfo> GetMailAccountAsync();

    /// <summary>Forgets the Outlook sign-in (the Dynamics one is untouched).</summary>
    Task SignOutMailAsync();

    /// <summary>The user's rules as the JSON file the service keeps.</summary>
    Task<string> GetRulesJsonAsync();

    /// <summary>Checks the rules and, only if they are valid, makes them the ones in force at once.</summary>
    Task<RulesSaveResult> SaveRulesJsonAsync(string json);

    /// <summary>Like <see cref="DryRunRulesAsync"/> but for rules not saved yet (read only). Throws if they are not valid.</summary>
    Task<DryRunResult> DryRunDraftAsync(string rulesJson);

    /// <summary>The user's notification texts as the JSON file the service keeps.</summary>
    Task<string> GetTemplatesJsonAsync();

    /// <summary>Checks the notification texts and, only if they are valid, makes them the ones in force at once.</summary>
    Task<RulesSaveResult> SaveTemplatesJsonAsync(string json);

    /// <summary>Names for the queues, teams and users the active tickets point at, so rules can be written by name.</summary>
    Task<IReadOnlyList<LookupItem>> GetLookupsAsync();

    /// <summary>Something changed: ask again. Sent to every connected app of that profile.</summary>
    event EventHandler<ChangedEventArgs>? Changed;
}
