using System.Globalization;
using System.Text.Json;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Sources;

namespace LiveHelpDesk.Dynamics;

/// <summary>Reads tickets and messages from Dynamics 365 through the Dataverse Web API.</summary>
/// <param name="currentUser">The signed-in user, to tell our own notes from the customer's.</param>
public sealed class DynamicsTicketSource(DataverseClient client, DynamicsFieldMap map, Func<Guid?> currentUser) : ITicketSource
{
    private const int IdBatch = 20;

    private string TicketSelect => $"$select={string.Join(',', map.TicketColumns)}";

    public async Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken)
    {
        await client.GetAsync("WhoAmI", cancellationToken);
        return client.LastServerDate ?? DateTimeOffset.UtcNow;
    }

    /// <summary>The signed-in user's id, from <c>WhoAmI</c>.</summary>
    public async Task<Guid> WhoAmIAsync(CancellationToken cancellationToken)
    {
        var me = await client.GetAsync("WhoAmI", cancellationToken);
        return Guid.Parse(me.GetProperty("UserId").GetString()!);
    }

    public async Task<IReadOnlyList<TicketSnapshot>> GetActiveTicketsAsync(CancellationToken cancellationToken) =>
        await Tickets($"incidents?{TicketSelect}&$filter=statecode eq 0", cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetActiveTicketIdsAsync(CancellationToken cancellationToken)
    {
        var rows = await client.QueryAllAsync("incidents?$select=incidentid&$filter=statecode eq 0", cancellationToken);
        return rows.Select(r => Guid.Parse(r.GetProperty("incidentid").GetString()!)).ToList();
    }

    public async Task<IReadOnlyList<TicketSnapshot>> GetTicketsModifiedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        await Tickets($"incidents?{TicketSelect}&$filter=modifiedon ge {Literal(since)}&$orderby=modifiedon asc", cancellationToken);

    public async Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var tickets = new List<TicketSnapshot>();
        foreach (var batch in ids.Distinct().Chunk(IdBatch))
        {
            var filter = string.Join(" or ", batch.Select(id => $"incidentid eq {id:D}"));
            tickets.AddRange(await Tickets($"incidents?{TicketSelect}&$filter={filter}", cancellationToken));
        }

        return tickets;
    }

    public async Task<IReadOnlyList<MessageSnapshot>> GetMessagesCreatedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var from = Literal(since);
        var emails = await client.QueryAllAsync(
            "emails?$select=activityid,createdon,directioncode,_regardingobjectid_value,sender,subject" +
            $"&$filter=createdon ge {from} and _regardingobjectid_value ne null&$orderby=createdon asc",
            cancellationToken);
        var notes = await client.QueryAllAsync(
            "annotations?$select=annotationid,createdon,_objectid_value,subject,_createdby_value" +
            $"&$filter=createdon ge {from} and _objectid_value ne null&$orderby=createdon asc",
            cancellationToken);

        var me = currentUser();
        return emails.Select(DataverseMapper.Email)
            .Concat(notes.Select(row => DataverseMapper.Note(row, me)))
            .OfType<MessageSnapshot>()
            .ToList();
    }

    private async Task<IReadOnlyList<TicketSnapshot>> Tickets(string url, CancellationToken cancellationToken)
    {
        var rows = await client.QueryAllAsync(url, cancellationToken);
        return rows.Select(row => DataverseMapper.Ticket(row, map)).ToList();
    }

    /// <summary>OData date literal: UTC, millisecond precision, not quoted.</summary>
    internal static string Literal(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
