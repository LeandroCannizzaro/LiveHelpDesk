using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LiveHelpDesk.Core.Sources;

namespace LiveHelpDesk.Dynamics;

public interface ITokenProvider
{
    /// <param name="forceRefresh">Ask the identity provider for a new token even if the cached one looks valid.</param>
    /// <exception cref="TicketSourceException">With <see cref="SourceFailure.AuthRequired"/> when the user must sign in again.</exception>
    Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal Dataverse Web API reader: paging, retries with back-off, and failures reported as
/// <see cref="TicketSourceException"/> so callers can tell "sign in again" from "try later" from "bug".
/// </summary>
public sealed class DataverseClient(
    HttpClient http,
    ITokenProvider tokens,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    int maxAttempts = 5)
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>The server's clock from the last response; watermarks are based on it.</summary>
    public DateTimeOffset? LastServerDate { get; private set; }

    /// <summary>Every row of a collection query, following <c>@odata.nextLink</c>.</summary>
    public async IAsyncEnumerable<JsonElement> QueryAsync(string url, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = url;
        while (next is not null)
        {
            var page = await GetAsync(next, cancellationToken);
            if (page.TryGetProperty("value", out var rows))
            {
                foreach (var row in rows.EnumerateArray())
                {
                    yield return row;
                }
            }

            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    public async Task<IReadOnlyList<JsonElement>> QueryAllAsync(string url, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        await foreach (var row in QueryAsync(url, cancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    public async Task<JsonElement> GetAsync(string url, CancellationToken cancellationToken)
    {
        var forceRefresh = false;
        var refreshedAfter401 = false;

        for (var attempt = 1; ; attempt++)
        {
            var token = await tokens.GetTokenAsync(forceRefresh, cancellationToken);
            forceRefresh = false;

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(Request(url, token), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= maxAttempts)
                {
                    throw new TicketSourceException(SourceFailure.Unavailable, $"Dynamics non raggiungibile: {ex.Message}", inner: ex);
                }

                await _delay(Backoff(attempt), cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.Headers.Date is { } date)
                {
                    LastServerDate = date;
                }

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    return document.RootElement.Clone();
                }

                var message = await ErrorMessageAsync(response, cancellationToken);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        if (refreshedAfter401)
                        {
                            throw new TicketSourceException(SourceFailure.AuthRequired, "Dynamics ha rifiutato il token: serve un nuovo accesso.");
                        }

                        refreshedAfter401 = true;
                        forceRefresh = true;
                        attempt--; // a refresh is not a retry
                        continue;

                    case HttpStatusCode.Forbidden:
                        throw new TicketSourceException(SourceFailure.Forbidden, $"Permessi insufficienti: {message}");

                    case HttpStatusCode.TooManyRequests:
                        if (attempt >= maxAttempts)
                        {
                            throw new TicketSourceException(SourceFailure.Throttled, "Dynamics limita le richieste.", RetryAfter(response));
                        }

                        await _delay(RetryAfter(response) ?? Backoff(attempt), cancellationToken);
                        continue;

                    case HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout:
                        if (attempt >= maxAttempts)
                        {
                            throw new TicketSourceException(SourceFailure.Unavailable, $"Dynamics risponde {(int)response.StatusCode}: {message}");
                        }

                        await _delay(RetryAfter(response) ?? Backoff(attempt), cancellationToken);
                        continue;

                    default:
                        throw new TicketSourceException(SourceFailure.Error, $"Richiesta rifiutata ({(int)response.StatusCode}): {message}");
                }
            }
        }
    }

    private static HttpRequestMessage Request(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.RelativeOrAbsolute));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Prefer", "odata.maxpagesize=500, odata.include-annotations=\"Microsoft.Dynamics.CRM.lookuplogicalname,OData.Community.Display.V1.FormattedValue\"");
        return request;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
        return wait is { } w ? TimeSpan.FromTicks(Math.Clamp(w.Ticks, 0, MaxWait.Ticks)) : null;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(MaxWait.TotalSeconds, Math.Pow(2, attempt - 1)));

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                ? message.GetString() ?? response.ReasonPhrase ?? ""
                : response.ReasonPhrase ?? "";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "";
        }
    }
}
