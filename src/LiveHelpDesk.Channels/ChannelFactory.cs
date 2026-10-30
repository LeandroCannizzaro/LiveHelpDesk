using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Channels;

public static class ChannelFactory
{
    /// <summary>The channels that are switched on in <paramref name="settings"/>, by id. The rest simply do not exist.</summary>
    public static IReadOnlyDictionary<string, INotificationChannel> Create(
        ChannelSettings settings, HttpClient http, IEmailSender? emailSender = null, Uri? twilioBase = null)
    {
        var channels = new Dictionary<string, INotificationChannel>(StringComparer.OrdinalIgnoreCase);

        if (settings.Email.Enabled)
        {
            channels[ChannelIds.Email] = new EmailChannel(settings.Email, emailSender ?? new MailKitSender(settings.Email));
        }

        if (settings.Teams.Enabled)
        {
            channels[ChannelIds.Teams] = new WebhookChannel(ChannelIds.Teams, settings.Teams, http);
        }

        if (settings.Sms.Enabled)
        {
            channels[ChannelIds.Sms] = new TwilioChannel(TwilioKind.Sms, settings.Sms, http, twilioBase);
        }

        if (settings.WhatsApp.Enabled)
        {
            channels[ChannelIds.WhatsApp] = new TwilioChannel(TwilioKind.WhatsApp, settings.WhatsApp, http, twilioBase);
        }

        return channels;
    }
}
