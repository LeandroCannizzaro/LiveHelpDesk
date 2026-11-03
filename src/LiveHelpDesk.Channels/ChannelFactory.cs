using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Channels;

public static class ChannelFactory
{
    /// <summary>The channels that are switched on in <paramref name="settings"/>, by id. The rest simply do not exist.</summary>
    /// <param name="outlookTokens">The Outlook sign-in; without it the Outlook provider reports that it cannot send.</param>
    public static IReadOnlyDictionary<string, INotificationChannel> Create(
        ChannelSettings settings, HttpClient http, IEmailSender? emailSender = null, Uri? twilioBase = null,
        IMailTokenSource? outlookTokens = null)
    {
        var channels = new Dictionary<string, INotificationChannel>(StringComparer.OrdinalIgnoreCase);

        if (settings.Email.Enabled)
        {
            var sender = emailSender ?? (settings.Email.Provider == EmailProvider.Outlook
                ? new GraphMailSender(http, outlookTokens ?? new NoOutlook(), () => settings.Email.Outlook.SaveToSentItems)
                : new MailKitSender(settings.Email));
            channels[ChannelIds.Email] = new EmailChannel(settings.Email, sender);
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

    private sealed class NoOutlook : IMailTokenSource
    {
        public bool KnownSignedOut => true;

        public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            throw new MailAuthException(MailAuthFailure.AuthRequired, "Serve l'accesso a Outlook: accedere dalle impostazioni del canale e-mail.");

        public Task<string?> AccountNameAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
