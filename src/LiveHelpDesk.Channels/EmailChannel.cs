using LiveHelpDesk.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LiveHelpDesk.Channels;

public sealed record EmailMessage(string From, IReadOnlyList<string> To, string Subject, string TextBody);

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
        if (string.IsNullOrWhiteSpace(settings.From) || settings.To.Count == 0 || string.IsNullOrWhiteSpace(settings.Host))
        {
            return SendResult.Fail("Configurazione e-mail incompleta (server, mittente e destinatari).");
        }

        var subject = notification.Severity == Severity.Critical ? $"[URGENTE] {notification.Title}" : notification.Title;
        var body = notification.Url is null
            ? notification.Body
            : $"{notification.Body}\n\nApri il ticket: {notification.Url}";

        try
        {
            await sender.SendAsync(new EmailMessage(settings.From, settings.To, subject, body), cancellationToken);
            return SendResult.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return SendResult.Fail($"Invio e-mail non riuscito: {ex.Message}");
        }
    }

    public Task<SendResult> TestAsync(CancellationToken cancellationToken) =>
        SendAsync(new Notification("LiveHelpDesk: messaggio di prova", "Se leggi questo messaggio, il canale e-mail funziona.", Severity.Info), cancellationToken);
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
        mime.Body = new TextPart("plain") { Text = message.TextBody };

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
