using LiveHelpDesk.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LiveHelpDesk.Channels;

/// <param name="HtmlBody">When set the message is sent as HTML, with <paramref name="TextBody"/> as the plain alternative.</param>
public sealed record EmailMessage(string From, IReadOnlyList<string> To, string Subject, string TextBody, string? HtmlBody = null);

/// <summary>The part of sending an e-mail that touches the network; faked in tests of the channel itself.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class EmailChannel(EmailSettings settings, IEmailSender sender) : INotificationChannel
{
    public string Id => ChannelIds.Email;

    public async Task<SendResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        // With Outlook the sender is the signed-in account and no recipient means that same account.
        var incomplete = settings.Provider != EmailProvider.Outlook
            && (string.IsNullOrWhiteSpace(settings.From) || settings.To.Count == 0 || string.IsNullOrWhiteSpace(settings.Host));
        if (incomplete)
        {
            return SendResult.Fail("Configurazione e-mail incompleta (server, mittente e destinatari).");
        }

        var subject = notification.Severity == Severity.Critical ? $"[URGENTE] {notification.Title}" : notification.Title;
        var body = notification.Url is null || Mentions(notification.Body, notification.Url)
            ? notification.Body
            : $"{notification.Body}\n\nApri il ticket: {notification.Url}";
        var html = notification.HtmlBody is null ? null : Html(notification.HtmlBody, notification.Url);

        try
        {
            await sender.SendAsync(new EmailMessage(settings.From, settings.To, subject, body, html), cancellationToken);
            return SendResult.Ok;
        }
        catch (MailAuthException ex)
        {
            return SendResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return SendResult.Fail($"Invio e-mail non riuscito: {ex.Message}");
        }
    }

    public Task<SendResult> TestAsync(CancellationToken cancellationToken) =>
        SendAsync(new Notification("LiveHelpDesk: messaggio di prova", "Se leggi questo messaggio, il canale e-mail funziona.", Severity.Info), cancellationToken);

    internal static string Html(string fragment, Uri? url)
    {
        var link = url is null || Mentions(fragment, url)
            ? ""
            : $"<p style=\"margin-top:16px\"><a href=\"{System.Net.WebUtility.HtmlEncode(url.AbsoluteUri)}\">Apri il ticket</a></p>";
        return $"<!DOCTYPE html><html><body style=\"font-family:'Segoe UI',Arial,sans-serif;font-size:14px\">{fragment}{link}</body></html>";
    }

    /// <summary>A custom text may already carry the ticket's link: then it is not added a second time.</summary>
    internal static bool Mentions(string text, Uri url) =>
        text.Contains(url.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
        || text.Contains(System.Net.WebUtility.HtmlEncode(url.AbsoluteUri), StringComparison.OrdinalIgnoreCase)
        || text.Contains(url.ToString(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>SMTP through MailKit.</summary>
public sealed class MailKitSender(EmailSettings settings) : IEmailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(message.From));
        foreach (var to in message.To)
        {
            mime.To.Add(MailboxAddress.Parse(to));
        }

        mime.Subject = message.Subject;
        mime.Body = message.HtmlBody is null
            ? new TextPart("plain") { Text = message.TextBody }
            : new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        var security = settings.Security switch
        {
            EmailSecurity.None => SecureSocketOptions.None,
            EmailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };

        await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(settings.User))
        {
            await client.AuthenticateAsync(settings.User, settings.Password ?? "", cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
