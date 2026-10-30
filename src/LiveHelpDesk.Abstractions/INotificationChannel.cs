namespace LiveHelpDesk.Abstractions;

/// <param name="Url">Deep link to the record, when there is one.</param>
public sealed record Notification(string Title, string Body, Severity Severity, Uri? Url = null);

public sealed record SendResult(bool Success, string? Error = null)
{
    public static SendResult Ok { get; } = new(true);

    public static SendResult Fail(string error) => new(false, error);
}

/// <summary>One outbound channel (e-mail, Teams, SMS, WhatsApp...). Configuration is channel-specific.</summary>
public interface INotificationChannel
{
    string Id { get; }

    Task<SendResult> SendAsync(Notification notification, CancellationToken cancellationToken);

    /// <summary>Sends a harmless test message so the user can check the channel is configured.</summary>
    Task<SendResult> TestAsync(CancellationToken cancellationToken);
}
