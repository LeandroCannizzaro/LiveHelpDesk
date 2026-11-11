using System.Net;
using System.Text;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Sources;
using LiveHelpDesk.Dynamics;
using Xunit;

namespace LiveHelpDesk.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(Uri Uri, string? Authorization, string? Prefer)> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Prefer", out var v) ? string.Join(",", v) : null));
        return respond(request, Requests.Count);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class FakeTokens : ITokenProvider
{
    public List<bool> Requests { get; } = [];

    public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Requests.Add(forceRefresh);
        return Task.FromResult(forceRefresh ? "fresh-token" : "token");
    }
}

public class DataverseClientTests
{
    private static readonly Uri Api = new("https://org.crm4.dynamics.com/api/data/v9.2/");

    private readonly List<TimeSpan> _waits = [];
    private readonly FakeTokens _tokens = new();

    private DataverseClient Client(FakeHandler handler, int maxAttempts = 5) =>
        new(new HttpClient(handler) { BaseAddress = Api }, _tokens, (wait, _) => { _waits.Add(wait); return Task.CompletedTask; }, maxAttempts);

    [Fact]
    public async Task ARequest_CarriesTheBearerToken_AndAsksForLookupTypes()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{ "value": [] }"""));

        await Client(handler).GetAsync("incidents", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer token", request.Authorization);
        Assert.Contains("lookuplogicalname", request.Prefer);
        Assert.Equal("https://org.crm4.dynamics.com/api/data/v9.2/incidents", request.Uri.ToString());
    }

    [Fact]
    public async Task Paging_FollowsNextLinkUntilTheLastPage()
    {
        var handler = new FakeHandler((request, n) => n switch
        {
            1 => FakeHandler.Json("""{ "value": [ {"n":1}, {"n":2} ], "@odata.nextLink": "https://org.crm4.dynamics.com/api/data/v9.2/incidents?$skiptoken=abc" }"""),
            _ => FakeHandler.Json("""{ "value": [ {"n":3} ] }"""),
        });

        var rows = await Client(handler).QueryAllAsync("incidents", CancellationToken.None);

        Assert.Equal([1, 2, 3], rows.Select(r => r.GetProperty("n").GetInt32()).ToArray());
        Assert.Contains("$skiptoken=abc", handler.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task TheServerClock_IsTakenFromTheDateHeader()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = FakeHandler.Json("""{ "value": [] }""");
            response.Headers.Date = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
            return response;
        });
        var client = Client(handler);

        await client.GetAsync("WhoAmI", CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero), client.LastServerDate);
    }

    [Fact]
    public async Task TooManyRequests_WaitsAsLongAsTheServerAsks_ThenSucceeds()
    {
        var handler = new FakeHandler((_, n) =>
        {
            if (n > 1)
            {
                return FakeHandler.Json("""{ "value": [] }""");
            }

            var throttled = FakeHandler.Json("{}", HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return throttled;
        });

        await Client(handler).GetAsync("incidents", CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(7)], _waits);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ServerErrors_AreRetriedWithGrowingWaits_ThenReportedAsUnavailable()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{ "error": { "message": "down" } }""", HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<TicketSourceException>(() => Client(handler, maxAttempts: 4).GetAsync("incidents", CancellationToken.None));

        Assert.Equal(SourceFailure.Unavailable, ex.Failure);
        Assert.Contains("down", ex.Message);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _waits);
    }

    [Fact]
    public async Task PersistentThrottling_IsReportedAsThrottled()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{}", HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<TicketSourceException>(() => Client(handler, maxAttempts: 2).GetAsync("incidents", CancellationToken.None));

        Assert.Equal(SourceFailure.Throttled, ex.Failure);
    }

    [Fact]
    public async Task ANetworkError_IsRetried_ThenReportedAsUnavailable()
    {
        var attempts = 0;
        var handler = new FakeHandler((_, _) =>
        {
            attempts++;
            throw new HttpRequestException("no route");
        });

        var ex = await Assert.ThrowsAsync<TicketSourceException>(() => Client(handler, maxAttempts: 3).GetAsync("incidents", CancellationToken.None));

        Assert.Equal(SourceFailure.Unavailable, ex.Failure);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task AnUnauthorizedAnswer_TriggersOneForcedTokenRefresh()
    {
        var handler = new FakeHandler((request, n) => n == 1
            ? FakeHandler.Json("{}", HttpStatusCode.Unauthorized)
            : FakeHandler.Json("""{ "value": [] }"""));

        await Client(handler).GetAsync("incidents", CancellationToken.None);

        Assert.Equal([false, true], _tokens.Requests);
        Assert.Equal("Bearer fresh-token", handler.Requests[1].Authorization);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task AnUnauthorizedAnswer_EvenWithAFreshToken_MeansTheUserMustSignInAgain()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{}", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<TicketSourceException>(() => Client(handler).GetAsync("incidents", CancellationToken.None));

        Assert.Equal(SourceFailure.AuthRequired, ex.Failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Forbidden_ExplainsWhichPermissionIsMissing_AndIsNotRetried()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{ "error": { "message": "prvReadIncident missing" } }""", HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<TicketSourceException>(() => Client(handler).GetAsync("incidents", CancellationToken.None));

        Assert.Equal(SourceFailure.Forbidden, ex.Failure);
        Assert.Contains("prvReadIncident", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ARejectedQuery_IsABug_NotAnOutage()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{ "error": { "message": "Could not find a property named 'nope'" } }""", HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<TicketSourceException>(() => Client(handler).GetAsync("incidents?$select=nope", CancellationToken.None));

        Assert.Equal(SourceFailure.Error, ex.Failure);
        Assert.Contains("nope", ex.Message);
        Assert.Single(handler.Requests);
    }
}

public class DataverseMapperTests
{
    private static System.Text.Json.JsonElement Parse(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    private const string TicketRow =
        """
        { "incidentid": "11111111-2222-3333-4444-555555555555", "ticketnumber": "261019389", "title": "data fattura",
          "statecode": 0, "statuscode": 717600002, "prioritycode": 1,
          "createdon": "2026-10-30T14:13:38Z", "modifiedon": "2026-10-30T15:47:49Z",
          "_ownerid_value": "aaaaaaaa-0000-0000-0000-000000000001", "_gs_codateam_value": "bbbbbbbb-0000-0000-0000-000000000002",
          "_gs_utentediliv2_value": null, "_gs_cliente_value": "cccccccc-0000-0000-0000-000000000003",
          "gs_statocomunicazioni": 717600002, "gs_sollecito": 717600000, "gs_insospeso": 717600001,
          "gs_applicativo": 717600005, "cr754_tipologiaticket": null,
          "_gs_cliente_value@OData.Community.Display.V1.FormattedValue": "Rossi Forniture S.r.l.",
          "_ownerid_value@OData.Community.Display.V1.FormattedValue": "Luca Bianchi",
          "gs_applicativo@OData.Community.Display.V1.FormattedValue": "Fatturazione" }
        """;

    [Fact]
    public void ATicketRow_MapsToTheNormalizedSnapshot()
    {
        var ticket = DataverseMapper.Ticket(Parse(TicketRow), DynamicsFieldMap.Qgs);

        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), ticket.Id);
        Assert.Equal("261019389", ticket.Number);
        Assert.Equal(717600002, ticket.Status);
        Assert.Equal(1, ticket.Priority);
        Assert.Equal(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), ticket.QueueId);
        Assert.Null(ticket.Level2UserId);
        Assert.Equal(717600002, ticket.CommunicationStatus);
        Assert.True(ticket.Reminder);
        Assert.False(ticket.OnHold);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 14, 13, 38, TimeSpan.Zero), ticket.CreatedOn);
        Assert.Equal("717600005", ticket.Extra["applicativo"]);
        Assert.Null(ticket.Extra["tipologia"]);
        Assert.Equal("Rossi Forniture S.r.l.", ticket.Labels[TicketLabels.Customer]);
        Assert.Equal("Luca Bianchi", ticket.Labels[TicketLabels.Owner]);
        Assert.Equal("Fatturazione", ticket.Labels["applicativo"]);
        Assert.Null(ticket.Labels[TicketLabels.Queue]);
    }

    [Fact]
    public void ATicketRowWithMissingOptionalColumns_StillMaps()
    {
        var ticket = DataverseMapper.Ticket(
            Parse("""{ "incidentid": "11111111-2222-3333-4444-555555555555", "ticketnumber": "1", "title": "t", "statecode": 0, "statuscode": 1, "createdon": "2026-10-30T14:00:00Z", "modifiedon": "2026-10-30T14:00:00Z" }"""),
            DynamicsFieldMap.Qgs);

        Assert.Null(ticket.OwnerId);
        Assert.Null(ticket.CommunicationStatus);
        Assert.False(ticket.Reminder);
        Assert.Equal(2, ticket.Priority);
    }

    private static string EmailRow(string direction, string regarding = "incident") =>
        $$"""
        { "activityid": "99999999-0000-0000-0000-000000000001", "createdon": "2026-10-30T15:53:00Z", "directioncode": {{direction}},
          "_regardingobjectid_value": "11111111-2222-3333-4444-555555555555",
          "_regardingobjectid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "{{regarding}}",
          "sender": "mario@cliente.it", "subject": "Re: fattura" }
        """;

    [Fact]
    public void ADirectionCodeOfFalse_IsAnIncomingEmail()
    {
        var message = DataverseMapper.Email(Parse(EmailRow("false")));

        Assert.NotNull(message);
        Assert.True(message.Incoming);
        Assert.Equal(MessageKind.Email, message.Kind);
        Assert.Equal("mario@cliente.it", message.From);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), message.TicketId);
    }

    [Fact]
    public void ADirectionCodeOfTrue_IsAnOutgoingEmail() =>
        Assert.False(DataverseMapper.Email(Parse(EmailRow("true")))!.Incoming);

    [Fact]
    public void AnEmailAboutSomethingElse_IsIgnored() =>
        Assert.Null(DataverseMapper.Email(Parse(EmailRow("false", regarding: "account"))));

    [Fact]
    public void ANoteWrittenByTheSignedInUser_IsOurs_AnyoneElsesIsIncoming()
    {
        var me = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        string Note(string author) =>
            $$"""{ "annotationid": "88888888-0000-0000-0000-000000000001", "createdon": "2026-10-30T15:00:00Z", "_objectid_value": "11111111-2222-3333-4444-555555555555", "_objectid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "incident", "_createdby_value": "{{author}}", "subject": "nota" }""";

        Assert.False(DataverseMapper.Note(Parse(Note(me.ToString())), me)!.Incoming);
        Assert.True(DataverseMapper.Note(Parse(Note("dddddddd-0000-0000-0000-000000000009")), me)!.Incoming);
    }
}

public class DynamicsTicketSourceTests
{
    private static readonly Uri Api = new("https://org.crm4.dynamics.com/api/data/v9.2/");

    private static (DynamicsTicketSource Source, FakeHandler Handler) Create(Func<HttpRequestMessage, int, HttpResponseMessage>? respond = null)
    {
        var handler = new FakeHandler(respond ?? ((_, _) => FakeHandler.Json("""{ "value": [] }""")));
        var client = new DataverseClient(new HttpClient(handler) { BaseAddress = Api }, new FakeTokens());
        return (new DynamicsTicketSource(client, DynamicsFieldMap.Qgs, () => null), handler);
    }

    private static string Url(FakeHandler handler, int index) => Uri.UnescapeDataString(handler.Requests[index].Uri.ToString());

    [Fact]
    public async Task ModifiedSince_FiltersOnTheServerSideTimestamp_OldestFirst()
    {
        var (source, handler) = Create();

        await source.GetTicketsModifiedSinceAsync(new DateTimeOffset(2026, 10, 30, 15, 30, 0, TimeSpan.FromHours(1)), CancellationToken.None);

        var url = Url(handler, 0);
        Assert.Contains("modifiedon ge 2026-10-30T14:30:00.000Z", url);
        Assert.Contains("$orderby=modifiedon asc", url);
        Assert.Contains("_gs_codateam_value", url);
        Assert.Contains("gs_statocomunicazioni", url);
    }

    private static (DynamicsTicketSource Source, FakeHandler Handler) CreateScoped(params Guid[] queues)
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{ "value": [] }"""));
        var client = new DataverseClient(new HttpClient(handler) { BaseAddress = Api }, new FakeTokens());
        return (new DynamicsTicketSource(client, DynamicsFieldMap.Qgs, () => null, queues), handler);
    }

    [Fact]
    public async Task WithQueues_EveryScopedTicketQuery_IsRestrictedOnTheServer()
    {
        var (source, handler) = CreateScoped(T.Id(1), T.Id(2));
        var scope = $"(_gs_codateam_value eq {T.Id(1):D} or _gs_codateam_value eq {T.Id(2):D})";

        await source.GetActiveTicketsAsync(CancellationToken.None);
        await source.GetActiveTicketIdsAsync(CancellationToken.None);
        await source.GetTicketsModifiedSinceAsync(T.Baseline, CancellationToken.None);
        await source.GetTicketsInScopeAsync([T.Id(7)], CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Contains($" and {scope}", Url(handler, i));
        }
    }

    [Fact]
    public async Task FetchingByIds_IsNotRestricted()
    {
        var (source, handler) = CreateScoped(T.Id(1));

        await source.GetTicketsAsync([T.Id(7)], CancellationToken.None);

        Assert.DoesNotContain("_gs_codateam_value eq", Url(handler, 0));
    }

    [Fact]
    public async Task WithoutQueues_NoQueueConditionIsSent()
    {
        var (source, handler) = Create();

        await source.GetActiveTicketsAsync(CancellationToken.None);
        await source.GetTicketsInScopeAsync([T.Id(7)], CancellationToken.None);

        Assert.DoesNotContain("_gs_codateam_value eq", Url(handler, 0));
        Assert.DoesNotContain("_gs_codateam_value eq", Url(handler, 1));
    }

    [Fact]
    public async Task ActiveTickets_AskForStateZero()
    {
        var (source, handler) = Create();

        await source.GetActiveTicketsAsync(CancellationToken.None);

        Assert.Contains("$filter=statecode eq 0", Url(handler, 0));
    }

    [Fact]
    public async Task ManyIds_AreFetchedInBatchesOfTwenty()
    {
        var (source, handler) = Create();
        var ids = Enumerable.Range(1, 25).Select(T.Id).ToList();

        await source.GetTicketsAsync(ids, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(20, Url(handler, 0).Split(" or ").Length);
        Assert.Equal(5, Url(handler, 1).Split(" or ").Length);
    }

    [Fact]
    public async Task Messages_ComeFromEmailsAndNotes_AndIgnoreWhatIsNotAboutACase()
    {
        var (source, handler) = Create((request, _) => request.RequestUri!.AbsolutePath.EndsWith("emails")
            ? FakeHandler.Json(
                """
                { "value": [
                  { "activityid": "99999999-0000-0000-0000-000000000001", "createdon": "2026-10-30T15:53:00Z", "directioncode": false,
                    "_regardingobjectid_value": "11111111-2222-3333-4444-555555555555",
                    "_regardingobjectid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "incident", "subject": "a" },
                  { "activityid": "99999999-0000-0000-0000-000000000002", "createdon": "2026-10-30T15:54:00Z", "directioncode": false,
                    "_regardingobjectid_value": "22222222-2222-3333-4444-555555555555",
                    "_regardingobjectid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "account", "subject": "b" } ] }
                """)
            : FakeHandler.Json(
                """
                { "value": [ { "annotationid": "88888888-0000-0000-0000-000000000001", "createdon": "2026-10-30T15:55:00Z",
                  "_objectid_value": "11111111-2222-3333-4444-555555555555",
                  "_objectid_value@Microsoft.Dynamics.CRM.lookuplogicalname": "incident", "_createdby_value": "dddddddd-0000-0000-0000-000000000009" } ] }
                """));

        var messages = await source.GetMessagesCreatedSinceAsync(T.Baseline, CancellationToken.None);

        Assert.Equal([MessageKind.Email, MessageKind.Note], messages.Select(m => m.Kind).ToArray());
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("emails"));
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("annotations"));
    }

    [Fact]
    public async Task ServerTime_ComesFromTheDateHeaderOfWhoAmI()
    {
        var (source, _) = Create((_, _) =>
        {
            var response = FakeHandler.Json("""{ "UserId": "11111111-2222-3333-4444-555555555555" }""");
            response.Headers.Date = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
            return response;
        });

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero), await source.GetServerTimeAsync(CancellationToken.None));
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), await source.WhoAmIAsync(CancellationToken.None));
    }
}
