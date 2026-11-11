using System.Globalization;
using System.Text.Json;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Dynamics;

/// <summary>Dataverse JSON rows to the normalized model. Pure functions: easy to pin down with real payload shapes.</summary>
public static class DataverseMapper
{
    private const string LogicalNameAnnotation = "@Microsoft.Dynamics.CRM.lookuplogicalname";
    private const string FormattedValueAnnotation = "@OData.Community.Display.V1.FormattedValue";

    public static TicketSnapshot Ticket(JsonElement row, DynamicsFieldMap map)
    {
        var extra = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [TicketLabels.Customer] = Formatted(row, DynamicsFieldMap.Lookup(map.Customer)),
            [TicketLabels.Owner] = Formatted(row, "_ownerid_value"),
            [TicketLabels.Queue] = Formatted(row, DynamicsFieldMap.Lookup(map.Queue)),
            [TicketLabels.Level2User] = Formatted(row, DynamicsFieldMap.Lookup(map.Level2User)),
            [TicketLabels.CommunicationStatus] = Formatted(row, map.CommunicationStatus),
            [TicketLabels.CreatedBy] = Formatted(row, "_createdby_value"),
            [TicketLabels.ModifiedBy] = Formatted(row, "_modifiedby_value"),
            [TicketLabels.State] = Formatted(row, "statecode"),
            [TicketLabels.Status] = Formatted(row, "statuscode"),
            [TicketLabels.Priority] = Formatted(row, "prioritycode"),
        };
        foreach (var (role, column) in map.Extras)
        {
            extra[role] = Text(row, column);
            labels[role] = Formatted(row, column);
        }

        return new TicketSnapshot(
            Guid.Parse(row.GetProperty("incidentid").GetString()!),
            Text(row, "ticketnumber") ?? "",
            Text(row, "title") ?? "",
            Int(row, "statecode") ?? 0,
            Int(row, "statuscode") ?? 0,
            Int(row, "prioritycode") ?? 2,
            Id(row, "_ownerid_value"),
            Id(row, DynamicsFieldMap.Lookup(map.Queue)),
            Id(row, DynamicsFieldMap.Lookup(map.Level2User)),
            Id(row, DynamicsFieldMap.Lookup(map.Customer)),
            Int(row, map.CommunicationStatus),
            Int(row, map.Reminder) == map.YesValue,
            Int(row, map.OnHold) == map.YesValue,
            Time(row, "createdon"),
            Time(row, "modifiedon"))
        {
            Extra = extra,
            Labels = labels,
        };
    }

    /// <summary>An e-mail about a case; null when it is about something else or about nothing.</summary>
    public static MessageSnapshot? Email(JsonElement row)
    {
        if (!RegardingIncident(row, "_regardingobjectid_value") || Id(row, "_regardingobjectid_value") is not { } ticket)
        {
            return null;
        }

        // directioncode: false = received (from the customer), true = sent (by us).
        var outgoing = row.TryGetProperty("directioncode", out var direction) && direction.ValueKind == JsonValueKind.True;
        return new MessageSnapshot(
            Guid.Parse(row.GetProperty("activityid").GetString()!),
            ticket,
            MessageKind.Email,
            Incoming: !outgoing,
            Time(row, "createdon"),
            Text(row, "sender"),
            Text(row, "subject"));
    }

    /// <summary>A note on a case. Notes written by the signed-in user are ours; anyone else's count as incoming.</summary>
    public static MessageSnapshot? Note(JsonElement row, Guid? me)
    {
        if (!RegardingIncident(row, "_objectid_value") || Id(row, "_objectid_value") is not { } ticket)
        {
            return null;
        }

        var author = Id(row, "_createdby_value");
        return new MessageSnapshot(
            Guid.Parse(row.GetProperty("annotationid").GetString()!),
            ticket,
            MessageKind.Note,
            Incoming: author is null || author != me,
            Time(row, "createdon"),
            Subject: Text(row, "subject"));
    }

    /// <summary>Rows come with the type of the thing they point at; only cases are ours.</summary>
    private static bool RegardingIncident(JsonElement row, string column) =>
        !row.TryGetProperty(column + LogicalNameAnnotation, out var type)
        || string.Equals(type.GetString(), "incident", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement row, string column)
    {
        if (!row.TryGetProperty(column, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>What Dynamics shows for the column (record name, option label); null when it sends nothing.</summary>
    private static string? Formatted(JsonElement row, string column) =>
        row.TryGetProperty(column + FormattedValueAnnotation, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement row, string column) =>
        row.TryGetProperty(column, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static Guid? Id(JsonElement row, string column) =>
        row.TryGetProperty(column, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;

    private static DateTimeOffset Time(JsonElement row, string column) =>
        row.TryGetProperty(column, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : DateTimeOffset.MinValue;
}
