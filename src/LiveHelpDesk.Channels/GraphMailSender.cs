using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LiveHelpDesk.Channels;

/// <summary>
/// Sends mail as the signed-in Microsoft 365 user through Microsoft Graph (<c>POST /me/sendMail</c>, permission
/// <c>Mail.Send</c>). The message comes from the account itself; with no recipients it goes to that same account.
/// </summary>
public sealed class GraphMailSender(
    HttpClient http,
    IMailTokenSource tokens,
    Func<bool> saveToSentItems,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IEmailSender
{
    public static readonly Uri SendMailUri = new("https://graph.microsoft.com/v1.0/me/sendMail");

    /// <summary>A throttled call is retried once if Graph asks for a short wait; longer than this is reported instead.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, ct) => Task.Delay(span, ct));

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var recipients = message.To.Count > 0
            ? message.To
            : [await tokens.AccountNameAsync(cancellationToken)
                ?? throw new MailAuthException(MailAuthFailure.AuthRequired, "Serve l'accesso a Outlook: accedere dalle impostazioni del canale e-mail.")];

        var body = JsonSerializer.Serialize(new
        {
            message = new
            {
                subject = message.Subject,
                body = new { contentType = "Text", content = message.TextBody },
                toRecipients = recipients.Select(address => new { emailAddress = new { address } }),
            },
            saveToSentItems = saveToSentItems(),
        });

        var refreshed = false;
        var throttled = false;
        while (true)
        {
            var token = await tokens.GetTokenAsync(refreshed, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, SendMailUri)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized when !refreshed:
                    refreshed = true; // the cached token may have been revoked: one new token, then give up
                    continue;

                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable when !throttled:
                    var wait = RetryAfter(response);
                    if (wait <= MaxWait)
                    {
                        throttled = true;
                        await _delay(wait, cancellationToken);
                        continue;
                    }

                    break;
            }

            throw new InvalidOperationException(await Describe(response));
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return TimeSpan.FromSeconds(2);
    }

    private static async Task<string> Describe(HttpResponseMessage response)
    {
        var detail = await ReadErrorAsync(response);
        var status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => $"Outlook ha rifiutato l'accesso (401): accedere di nuovo dalle impostazioni del canale e-mail. {detail}",
            HttpStatusCode.Forbidden =>
                $"Outlook non permette di inviare (403): manca il permesso Mail.Send o l'amministratore lo ha bloccato. {detail}",
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
                $"Outlook è sovraccarico ({status}), riprovo più tardi. {detail}",
            _ => $"Outlook ha risposto {status}. {detail}",
        };
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                return string.Create(CultureInfo.InvariantCulture, $"{code} {text}").Trim();
            }
        }
        catch (JsonException)
        {
            // Not a Graph error body: the status code alone will have to do.
        }

        return "";
    }
}
