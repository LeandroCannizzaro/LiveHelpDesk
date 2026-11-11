using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Channels;

public enum TwilioKind
{
    Sms,
    WhatsApp,
}

/// <summary>
/// Text messages or WhatsApp through Twilio's REST API, one request per recipient. WhatsApp messages that start a
/// conversation must use a template approved by Meta (<see cref="TwilioSettings.ContentSid"/>); that is a rule of
/// WhatsApp, not something this class can work around.
/// </summary>
public sealed class TwilioChannel(TwilioKind kind, TwilioSettings settings, HttpClient http, Uri? apiBase = null) : INotificationChannel
{
    private static readonly Uri DefaultApi = new("https://api.twilio.com/");

    private int MaxLength => kind == TwilioKind.Sms ? 320 : 1000;

    public string Id => kind == TwilioKind.Sms ? ChannelIds.Sms : ChannelIds.WhatsApp;

    public async Task<SendResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.AccountSid) || string.IsNullOrWhiteSpace(settings.AuthToken)
            || string.IsNullOrWhiteSpace(settings.From) || settings.To.Count == 0)
        {
            return SendResult.Fail("Configurazione incompleta (account, token, numero mittente e destinatari).");
        }

        var failures = new List<string>();
        foreach (var to in settings.To)
        {
            var result = await SendOneAsync(to, notification, cancellationToken);
            if (!result.Success)
            {
                failures.Add($"{Mask(to)}: {result.Error}");
            }
        }

        return failures.Count == 0 ? SendResult.Ok : SendResult.Fail(string.Join("; ", failures));
    }

    public Task<SendResult> TestAsync(CancellationToken cancellationToken) =>
        SendAsync(new Notification("LiveHelpDesk: messaggio di prova", "Il canale funziona.", Severity.Info), cancellationToken);

    private async Task<SendResult> SendOneAsync(string to, Notification notification, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["To"] = Address(to),
            ["From"] = Address(settings.From),
        };

        if (kind == TwilioKind.WhatsApp && !string.IsNullOrWhiteSpace(settings.ContentSid))
        {
            form["ContentSid"] = settings.ContentSid;
            form["ContentVariables"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["1"] = notification.Title, ["2"] = notification.Body });
        }
        else
        {
            form["Body"] = Text(notification);
        }

        var url = new Uri(apiBase ?? DefaultApi, $"2010-04-01/Accounts/{Uri.EscapeDataString(settings.AccountSid)}/Messages.json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.AccountSid}:{settings.AuthToken}")));

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return SendResult.Ok;
            }

            return SendResult.Fail(await ErrorAsync(response, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SendResult.Fail($"Twilio non raggiungibile: {ex.GetType().Name}");
        }
    }

    private string Address(string number) =>
        kind == TwilioKind.WhatsApp && !number.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ? $"whatsapp:{number}" : number;

    private string Text(Notification notification)
    {
        var text = string.IsNullOrWhiteSpace(notification.Body) ? notification.Title : $"{notification.Title}\n{notification.Body}";
        if (notification.Url is not null && !EmailChannel.Mentions(text, notification.Url))
        {
            text += $"\n{notification.Url}";
        }

        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var code = root.TryGetProperty("code", out var c) ? c.ToString() : null;
            return $"Twilio {(int)response.StatusCode}{(code is null ? "" : $" (codice {code})")}: {message}";
        }
        catch (JsonException)
        {
            return $"Twilio ha risposto {(int)response.StatusCode}";
        }
    }

    /// <summary>Phone numbers do not belong in logs or health messages in full.</summary>
    private static string Mask(string number) => number.Length <= 4 ? number : $"…{number[^4..]}";
}
