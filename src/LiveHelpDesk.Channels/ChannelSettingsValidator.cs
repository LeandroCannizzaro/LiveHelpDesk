using System.Text.RegularExpressions;
using LiveHelpDesk.Abstractions;
using MimeKit;

namespace LiveHelpDesk.Channels;

/// <summary>Catches the mistakes that would otherwise show up as a failed message weeks later. Only enabled channels are checked.</summary>
public static partial class ChannelSettingsValidator
{
    [GeneratedRegex(@"^(whatsapp:)?\+[1-9][0-9]{6,14}$")]
    private static partial Regex PhoneNumber();

    public static IReadOnlyList<string> Validate(ChannelSettings settings)
    {
        var errors = new List<string>();

        if (settings.Email.Enabled)
        {
            var email = settings.Email;
            if (email.Provider == EmailProvider.Outlook)
            {
                // The sender is the signed-in account; no server, user or password. No recipient means the account itself.
                if (email.To.Any(a => !IsAddress(a)))
                {
                    errors.Add("E-mail: tutti gli indirizzi dei destinatari devono essere validi.");
                }

                if (!IsDirectoryId(email.Outlook.TenantId))
                {
                    errors.Add("E-mail: l'ID tenant di Outlook deve essere un GUID o un dominio (es. azienda.onmicrosoft.com).");
                }

                if (!string.IsNullOrWhiteSpace(email.Outlook.ClientId) && !Guid.TryParse(email.Outlook.ClientId.Trim(), out _))
                {
                    errors.Add("E-mail: l'ID applicazione (client) di Outlook deve essere un GUID.");
                }
            }
            else
            {
                ValidateSmtp(email, errors);
            }
        }

        ValidateRest(settings, errors);
        return errors;
    }

    private static bool IsDirectoryId(string? text) =>
        string.IsNullOrWhiteSpace(text)
        || Guid.TryParse(text.Trim(), out _)
        || (text.Trim().Contains('.') && !text.Trim().Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'));

    private static void ValidateSmtp(EmailSettings email, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(email.Host))
        {
            errors.Add("E-mail: manca il server SMTP.");
        }

        if (email.Port is < 1 or > 65535)
        {
            errors.Add("E-mail: porta non valida.");
        }

        if (!IsAddress(email.From))
        {
            errors.Add("E-mail: indirizzo del mittente non valido.");
        }

        if (email.To.Count == 0 || email.To.Any(a => !IsAddress(a)))
        {
            errors.Add("E-mail: serve almeno un destinatario e tutti gli indirizzi devono essere validi.");
        }

        if (!string.IsNullOrEmpty(email.User) && string.IsNullOrEmpty(email.Password))
        {
            errors.Add("E-mail: è indicato l'utente ma manca la password.");
        }
    }

    private static void ValidateRest(ChannelSettings settings, List<string> errors)
    {
        if (settings.Teams.Enabled
            && (!Uri.TryCreate(settings.Teams.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("Teams: l'indirizzo del webhook deve essere un indirizzo https.");
        }

        Twilio("SMS", settings.Sms, requireTemplate: false, errors);
        Twilio("WhatsApp", settings.WhatsApp, requireTemplate: false, errors);

        foreach (var (channel, limit) in settings.MaxPerHour)
        {
            if (limit < 1)
            {
                errors.Add($"Limite orario del canale {channel}: deve essere almeno 1.");
            }
        }
    }

    private static void Twilio(string name, TwilioSettings settings, bool requireTemplate, List<string> errors)
    {
        if (!settings.Enabled)
        {
            return;
        }

        if (!settings.AccountSid.StartsWith("AC", StringComparison.Ordinal) || settings.AccountSid.Length < 10)
        {
            errors.Add($"{name}: l'Account SID di Twilio inizia con \"AC\".");
        }

        if (string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            errors.Add($"{name}: manca l'Auth Token di Twilio.");
        }

        if (!PhoneNumber().IsMatch(settings.From))
        {
            errors.Add($"{name}: il numero mittente deve essere nel formato internazionale (+39...).");
        }

        if (settings.To.Count == 0 || settings.To.Any(n => !PhoneNumber().IsMatch(n)))
        {
            errors.Add($"{name}: serve almeno un destinatario e tutti i numeri devono essere nel formato internazionale (+39...).");
        }

        if (requireTemplate && string.IsNullOrWhiteSpace(settings.ContentSid))
        {
            errors.Add($"{name}: serve il modello approvato (Content SID).");
        }
    }

    /// <summary>MimeKit is lenient (it accepts "broken" as a local name); a mail server is not, so require name@domain.</summary>
    private static bool IsAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !MailboxAddress.TryParse(text, out var mailbox))
        {
            return false;
        }

        var at = mailbox.Address.IndexOf('@');
        return at > 0 && at < mailbox.Address.Length - 1;
    }
}
