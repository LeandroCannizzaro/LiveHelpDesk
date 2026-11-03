using System.Globalization;
using System.Text.Json;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Sources;

namespace LiveHelpDesk.Dynamics;

/// <summary>Reads tickets and messages from Dynamics 365 through the Dataverse Web API.</summary>
/// <param name="currentUser">The signed-in user, to tell our own notes from the customer's.</param>
/// <param name="queues">The queues to monitor, filtered on the server; null or empty means every queue.</param>
public sealed class DynamicsTicketSource(
    DataverseClient client,
    DynamicsFieldMap map,
    Func<Guid?> currentUser,
    IReadOnlyCollection<Guid>? queues = null) : ITicketSource
{
    private const int IdBatch = 20;

    private string TicketSelect => $"$select={string.Join(',', map.TicketColumns)}";

    /// <summary>OData condition restricting tickets to the monitored queues, or null when every queue is monitored.</summary>
    private string? QueueScope =>
        queues is { Count: > 0 }
            ? "(" + string.Join(" or ", queues.Distinct().Select(id => $"{DynamicsFieldMap.Lookup(map.Queue)} eq {id:D}")) + ")"
            : null;

    private string And(string condition) => QueueScope is { } scope ? $"{condition} and {scope}" : condition;

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
        await Tickets($"incidents?{TicketSelect}&$filter={And("statecode eq 0")}", cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetActiveTicketIdsAsync(CancellationToken cancellationToken)
    {
        var rows = await client.QueryAllAsync($"incidents?$select=incidentid&$filter={And("statecode eq 0")}", cancellationToken);
        return rows.Select(r => Guid.Parse(r.GetProperty("incidentid").GetString()!)).ToList();
    }

    public async Task<IReadOnlyList<TicketSnapshot>> GetTicketsModifiedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        await Tickets($"incidents?{TicketSelect}&$filter={And($"modifiedon ge {Literal(since)}")}&$orderby=modifiedon asc", cancellationToken);

    public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        TicketsById(ids, scoped: false, cancellationToken);

    public Task<IReadOnlyList<TicketSnapshot>> GetTicketsInScopeAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        TicketsById(ids, scoped: true, cancellationToken);

    private async Task<IReadOnlyList<TicketSnapshot>> TicketsById(IReadOnlyCollection<Guid> ids, bool scoped, CancellationToken cancellationToken)
    {
        var tickets = new List<TicketSnapshot>();
        foreach (var batch in ids.Distinct().Chunk(IdBatch))
        {
            var filter = "(" + string.Join(" or ", batch.Select(id => $"incidentid eq {id:D}")) + ")";
            if (scoped)
            {
                filter = And(filter);
            }

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

    /// <summary>
    /// The names behind some ids. The same id space holds queues, teams and users, so each id is looked up in all three;
    /// ids that none of them knows are simply left out.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string Name, string Kind)>> ResolveNamesAsync(
        IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var found = new List<(Guid, string, string)>();
        foreach (var batch in ids.Distinct().Chunk(IdBatch))
        {
            foreach (var (set, key, column, kind) in new[]
            {
                ("queues", "queueid", "name", "queue"),
                ("teams", "teamid", "name", "team"),
                ("systemusers", "systemuserid", "fullname", "user"),
            })
            {
                var filter = string.Join(" or ", batch.Select(id => $"{key} eq {id:D}"));
                var rows = await client.QueryAllAsync($"{set}?$select={key},{column}&$filter={filter}", cancellationToken);
                foreach (var row in rows)
                {
                    if (row.TryGetProperty(key, out var id) && row.TryGetProperty(column, out var name) && name.GetString() is { Length: > 0 } text)
                    {
                        found.Add((Guid.Parse(id.GetString()!), text, kind));
                    }
                }
            }
        }

        return found;
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
