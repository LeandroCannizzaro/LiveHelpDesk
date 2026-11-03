namespace LiveHelpDesk.Abstractions;

public enum EmailSecurity
{
    None,

    /// <summary>Connect in the clear, then upgrade (port 587): the usual choice.</summary>
    StartTls,

    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,
}

/// <summary>How the e-mail channel sends.</summary>
public enum EmailProvider
{
    /// <summary>Through an SMTP server with user and password (the original way).</summary>
    Smtp,

    /// <summary>Through Microsoft Graph, signed in with the user's own Microsoft 365 account (no password stored).</summary>
    Outlook,
}

public enum WebhookFormat
{
    /// <summary>An Adaptive Card message, as expected by a Teams "Workflows" webhook.</summary>
    TeamsCard,

    /// <summary>A plain JSON object (title, body, severity, url) for any other receiver.</summary>
    Json,
}

/// <summary>Sending as the signed-in Microsoft 365 user. Nothing here is secret: the sign-in lives in an encrypted token cache.</summary>
public sealed class OutlookSettings
{
    /// <summary>Entra tenant (GUID or domain). Empty: the tenant of the Dynamics sign-in.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Public client application with the delegated Mail.Send permission. Empty: the Microsoft Graph Command Line Tools client.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Keep a copy of each message in the account's Sent Items.</summary>
    public bool SaveToSentItems { get; set; }
}

public sealed class EmailSettings
{
    public bool Enabled { get; set; }

    public EmailProvider Provider { get; set; } = EmailProvider.Smtp;

    /// <summary>Used when <see cref="Provider"/> is Outlook.</summary>
    public OutlookSettings Outlook { get; set; } = new();

    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    public EmailSecurity Security { get; set; } = EmailSecurity.StartTls;

    public string? User { get; set; }

    /// <summary>Secret: stored encrypted, never shown back.</summary>
    public string? Password { get; set; }

    public string From { get; set; } = "";

    public List<string> To { get; set; } = [];
}

public sealed class WebhookSettings
{
    public bool Enabled { get; set; }

    /// <summary>Secret: the address itself grants the right to post, so it is stored encrypted.</summary>
    public string Url { get; set; } = "";

    public WebhookFormat Format { get; set; } = WebhookFormat.TeamsCard;
}

/// <summary>One Twilio sender, used for text messages or for WhatsApp.</summary>
public sealed class TwilioSettings
{
    public bool Enabled { get; set; }

    public string AccountSid { get; set; } = "";

    /// <summary>Secret: stored encrypted, never shown back.</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>The sending number (E.164, e.g. +39...), or the approved WhatsApp sender.</summary>
    public string From { get; set; } = "";

    public List<string> To { get; set; } = [];

    /// <summary>WhatsApp only: the approved template used to open a conversation (business-initiated messages need one).</summary>
    public string? ContentSid { get; set; }
}

/// <summary>Everything the service needs to send through the outside channels, one set per user.</summary>
public sealed class ChannelSettings
{
    public EmailSettings Email { get; set; } = new();

    public WebhookSettings Teams { get; set; } = new();

    public TwilioSettings Sms { get; set; } = new();

    public TwilioSettings WhatsApp { get; set; } = new();

    /// <summary>Quiet hours per channel id (see <see cref="ChannelSchedule"/>).</summary>
    public Dictionary<string, ChannelSchedule> Schedules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most messages per hour for each channel; beyond it a burst is folded into one summary.</summary>
    public Dictionary<string, int> MaxPerHour { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["email"] = 30,
        ["teams"] = 60,
        ["sms"] = 10,
        ["whatsapp"] = 10,
    };
}
