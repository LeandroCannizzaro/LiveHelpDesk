using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Channels;

/// <summary>
/// Posts to a web address. With the Teams format it speaks to a Teams "Workflows" webhook (the old Office 365
/// connector webhooks are retired); with the JSON format it serves anything else that accepts a POST.
/// </summary>
public sealed class WebhookChannel(string id, WebhookSettings settings, HttpClient http) : INotificationChannel
{
    public string Id => id;

    public async Task<SendResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return SendResult.Fail("Indirizzo del webhook mancante o non https.");
        }

        var payload = settings.Format == WebhookFormat.TeamsCard ? TeamsCard(notification) : Plain(notification);

        try
        {
            using var response = await http.PostAsJsonAsync(url, payload, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return SendResult.Ok;
            }

            // The address is a credential: it must never end up in a message or a log, so only the status is reported.
            var detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return SendResult.Fail($"Il webhook ha risposto {(int)response.StatusCode}{(detail.Length == 0 ? "" : $": {Trim(detail, 200)}")}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SendResult.Fail($"Webhook non raggiungibile: {ex.GetType().Name}");
        }
    }

    public Task<SendResult> TestAsync(CancellationToken cancellationToken) =>
        SendAsync(new Notification("LiveHelpDesk: messaggio di prova", "Se vedi questo messaggio, il canale funziona.", Severity.Info), cancellationToken);

    internal static JsonObject TeamsCard(Notification notification)
    {
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = notification.Title,
                ["weight"] = "Bolder",
                ["size"] = "Medium",
                ["wrap"] = true,
                ["color"] = notification.Severity switch { Severity.Critical => "Attention", Severity.Attention => "Warning", _ => "Default" },
            },
        };

        if (!string.IsNullOrWhiteSpace(notification.Body))
        {
            body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = notification.Body, ["wrap"] = true });
        }

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };

        if (notification.Url is not null)
        {
            card["actions"] = new JsonArray
            {
                new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Apri il ticket", ["url"] = notification.Url.ToString() },
            };
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card },
            },
        };
    }

    internal static JsonObject Plain(Notification notification) =>
        new()
        {
            ["title"] = notification.Title,
            ["body"] = notification.Body,
            ["severity"] = notification.Severity.ToString(),
            ["url"] = notification.Url?.ToString(),
        };

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
