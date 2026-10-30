using System.Globalization;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

/// <summary>The fields a rule condition can look at, read from an event and the ticket it carries.</summary>
public static class RuleFields
{
    private static readonly Dictionary<string, Func<TicketEvent, EvaluationContext, object?>> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["number"] = (e, _) => e.Ticket?.Number ?? e.TicketNumber,
            ["title"] = (e, _) => e.Ticket?.Title ?? e.Title,
            ["priority"] = (e, _) => e.Ticket?.Priority,
            ["state"] = (e, _) => e.Ticket?.State,
            ["status"] = (e, _) => e.Ticket?.Status,
            ["level"] = (e, c) => e.Ticket is { } t && c.Mapping.StatusLevels.TryGetValue(t.Status, out var level) ? level : null,
            ["owner"] = (e, _) => e.Ticket?.OwnerId,
            ["queue"] = (e, _) => e.Ticket?.QueueId,
            ["level2user"] = (e, _) => e.Ticket?.Level2UserId,
            ["customer"] = (e, _) => e.Ticket?.CustomerId,
            ["commstatus"] = (e, _) => e.Ticket?.CommunicationStatus,
            ["onhold"] = (e, _) => e.Ticket?.OnHold,
            ["reminder"] = (e, _) => e.Ticket?.Reminder,
            ["ageminutes"] = (e, c) => e.Ticket is { } t ? (c.Now - t.CreatedOn).TotalMinutes : null,
            ["eventtype"] = (e, _) => e.Type.ToString(),
            ["subtype"] = (e, _) => e.Subtype,
            ["change"] = (e, _) => e.Change,
            ["message.kind"] = (e, _) => e.Message?.Kind.ToString(),
            ["message.incoming"] = (e, _) => e.Message?.Incoming,
            ["message.from"] = (e, _) => e.Message?.From,
            ["message.subject"] = (e, _) => e.Message?.Subject,
        };

    private const string ExtraPrefix = "extra.";

    public static IEnumerable<string> Names => Known.Keys;

    public static bool IsKnown(string field) =>
        Known.ContainsKey(field)
        || (field.StartsWith(ExtraPrefix, StringComparison.OrdinalIgnoreCase) && field.Length > ExtraPrefix.Length);

    /// <summary>The field as comparable text (numbers and booleans in invariant form), or null when absent.</summary>
    public static string? GetText(string field, TicketEvent evt, EvaluationContext context)
    {
        object? value;
        if (field.StartsWith(ExtraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string? extra = null;
            evt.Ticket?.Extra.TryGetValue(field[ExtraPrefix.Length..], out extra);
            value = extra;
        }
        else
        {
            value = Known.TryGetValue(field, out var read) ? read(evt, context) : null;
        }

        return value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}
