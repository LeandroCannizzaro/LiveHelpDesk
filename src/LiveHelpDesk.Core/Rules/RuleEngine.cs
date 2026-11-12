using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

/// <param name="MatchesByRule">Every rule id, including those that matched nothing.</param>
public sealed record DryRunReport(
    IReadOnlyList<RuleMatch> Matches,
    IReadOnlyDictionary<string, int> MatchesByRule,
    int EventsEvaluated,
    int EventsWithoutMatch);

/// <summary>Pure rule evaluation: no clock, no I/O, so every behaviour can be pinned down by a test.</summary>
public static class RuleEngine
{
    public static readonly IReadOnlySet<string> Operators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "in", "notIn", "contains", "startsWith", "regex", "gt", "gte", "lt", "lte", "isNull", "notNull",
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<RuleMatch> Evaluate(TicketEvent evt, IEnumerable<Rule> rules, EvaluationContext context) =>
        rules
            .Where(r => r.Enabled && r.Events.Contains(evt.Type) && Matches(r.When, evt, context))
            .Select(r => new RuleMatch(r, evt))
            .ToList();

    /// <summary>Runs the rules over past or hypothetical events and reports what would have been notified.</summary>
    public static DryRunReport DryRun(IEnumerable<Rule> rules, IEnumerable<TicketEvent> events, EvaluationContext context)
    {
        var ruleList = rules.ToList();
        var counts = ruleList.ToDictionary(r => r.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var matches = new List<RuleMatch>();
        var evaluated = 0;
        var withoutMatch = 0;

        foreach (var evt in events)
        {
            evaluated++;
            var hit = Evaluate(evt, ruleList, context);
            if (hit.Count == 0)
            {
                withoutMatch++;
            }

            foreach (var match in hit)
            {
                matches.Add(match);
                counts[match.Rule.Id]++;
            }
        }

        return new DryRunReport(matches, counts, evaluated, withoutMatch);
    }

    /// <summary>"What if each of these tickets had just been created?": lets a rule be tried on today's tickets.</summary>
    public static IEnumerable<TicketEvent> AsNewTicketEvents(IEnumerable<TicketSnapshot> tickets) =>
        tickets.Select(t => new TicketEvent(
            DedupKeys.Created(t.Id), TicketEventType.TicketCreated, t.CreatedOn, t.Id, t.Number, t.Title, Ticket: t));

    public static bool Matches(RuleCondition? condition, TicketEvent evt, EvaluationContext context)
    {
        if (condition is null)
        {
            return true;
        }

        if (condition.All is { Count: > 0 } all && !all.All(c => Matches(c, evt, context)))
        {
            return false;
        }

        if (condition.Any is { Count: > 0 } any && !any.Any(c => Matches(c, evt, context)))
        {
            return false;
        }

        if (condition.Not is { } not && Matches(not, evt, context))
        {
            return false;
        }

        // A custom expression is one more part that must hold, as if it were in brackets.
        if (condition.Expression is { } expression && !RuleExpression.Evaluate(expression, evt, context))
        {
            return false;
        }

        return condition.Field is null || Leaf(condition, evt, context);
    }

    private static bool Leaf(RuleCondition condition, TicketEvent evt, EvaluationContext context) =>
        Test(condition.Field!, condition.Op ?? "", Operands(condition.Value), evt, context);

    /// <summary>
    /// One comparison of a field with operands. Fields that have names (priority, queue, owner...) match either by
    /// code or by name ("Alta", "Supporto L1"); text operators look at the name when there is one.
    /// Called by the compiled custom expressions too.
    /// </summary>
    public static bool Test(string field, string op, IReadOnlyList<string?> operands, TicketEvent evt, EvaluationContext context)
    {
        var actual = RuleFields.GetText(field, evt, context);
        var name = RuleFields.GetName(field, evt, context);
        var shown = name ?? actual;
        var values = operands.Select(o => o is not null && o.Equals("$me", StringComparison.OrdinalIgnoreCase) ? context.MeId?.ToString("D") : o).ToList();
        var first = values.Count > 0 ? values[0] : null;

        bool Is(string? expected) =>
            (actual is not null && Equal(actual, expected)) || (name is not null && Equal(name, expected));

        return op.ToLowerInvariant() switch
        {
            "isnull" => string.IsNullOrEmpty(actual),
            "notnull" => !string.IsNullOrEmpty(actual),
            "eq" => Is(first),
            "ne" => !Is(first),
            "in" => values.Any(Is),
            "notin" => !values.Any(Is),
            "contains" => shown is not null && first is not null && shown.Contains(first, StringComparison.OrdinalIgnoreCase),
            "startswith" => shown is not null && first is not null && shown.StartsWith(first, StringComparison.OrdinalIgnoreCase),
            "regex" => shown is not null && first is not null && IsMatch(shown, first),
            "gt" => Compare(actual, first) is > 0,
            "gte" => Compare(actual, first) is >= 0,
            "lt" => Compare(actual, first) is < 0,
            "lte" => Compare(actual, first) is <= 0,
            _ => false,
        };
    }

    private static bool Equal(string actual, string? expected)
    {
        if (expected is null)
        {
            return false;
        }

        return TryNumber(actual, out var a) && TryNumber(expected, out var b)
            ? a == b
            : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int? Compare(string? actual, string? expected) =>
        TryNumber(actual, out var a) && TryNumber(expected, out var b) ? a.CompareTo(b) : null;

    private static bool TryNumber(string? text, out double number) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    public static bool IsMatch(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static List<string?> Operands(JsonNode? value)
    {
        var items = value is JsonArray array ? array.ToList() : [value];
        return items.Select(Operand).ToList();
    }

    private static string? Operand(JsonNode? node)
    {
        if (node is not JsonValue json)
        {
            return null;
        }

        return json.GetValueKind() switch
        {
            JsonValueKind.String => json.GetValue<string>(),
            JsonValueKind.Number => json.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
