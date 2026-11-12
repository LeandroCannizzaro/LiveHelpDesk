using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LiveHelpDesk.Core.Rules;

/// <param name="File">The parsed (and migrated) rules; null when the text could not be read at all.</param>
/// <param name="Errors">Human-readable problems; a file with errors must not replace the last good one.</param>
public sealed record RulesLoadResult(RulesFile? File, IReadOnlyList<string> Errors)
{
    public bool IsValid => File is not null && Errors.Count == 0;
}

/// <summary>Reading, migrating, validating and writing the versioned rules file.</summary>
public static class RulesConfig
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Migration N upgrades a file from version N to N+1.</summary>
    private static readonly Func<JsonNode, JsonNode>[] Migrations =
    [
        // v0 -> v1: early files were a bare list of rules, or an object without a version.
        node => node switch
        {
            JsonArray array => new JsonObject { ["version"] = 1, ["rules"] = array.DeepClone() },
            JsonObject obj => SetVersion(obj, 1),
            _ => node,
        },
    ];

    public static RulesLoadResult Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return Failed($"JSON non valido: {ex.Message}");
        }

        if (node is null)
        {
            return Failed("Il file è vuoto.");
        }

        var version = ReadVersion(node);
        if (version > CurrentVersion)
        {
            return Failed($"Il file è della versione {version}, più recente di quella supportata ({CurrentVersion}).");
        }

        for (; version < CurrentVersion; version++)
        {
            node = Migrations[version](node);
        }

        RulesFile? file;
        try
        {
            file = node.Deserialize<RulesFile>(Options);
        }
        catch (JsonException ex)
        {
            return Failed($"Struttura delle regole non valida: {ex.Message}");
        }

        return file is null ? Failed("Il file è vuoto.") : new RulesLoadResult(file, Validate(file));
    }

    public static string Serialize(RulesFile file) => JsonSerializer.Serialize(file, Options);

    public static IReadOnlyList<string> Validate(RulesFile file)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rule, index) in file.Rules.Select((r, i) => (r, i)))
        {
            var label = string.IsNullOrWhiteSpace(rule.Id) ? $"regola #{index + 1}" : $"regola '{rule.Id}'";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                errors.Add($"{label}: manca l'id.");
            }
            else if (!seen.Add(rule.Id))
            {
                errors.Add($"{label}: id duplicato.");
            }

            if (rule.Events.Count == 0)
            {
                errors.Add($"{label}: nessun evento selezionato.");
            }

            if (rule.Actions.Channels.Count == 0)
            {
                errors.Add($"{label}: nessun canale di notifica.");
            }

            if (rule.Actions.EscalateAfterMinutes is { } minutes)
            {
                if (minutes <= 0)
                {
                    errors.Add($"{label}: i minuti di escalation devono essere maggiori di zero.");
                }

                if (rule.Actions.EscalateChannels.Count == 0)
                {
                    errors.Add($"{label}: escalation senza canali.");
                }
            }

            if (rule.When is not null)
            {
                ValidateCondition(rule.When, label, errors);
            }
        }

        return errors;
    }

    private static void ValidateCondition(RuleCondition node, string label, List<string> errors)
    {
        var isLeaf = node.Field is not null || node.Op is not null;
        var isGroup = node.All is not null || node.Any is not null || node.Not is not null;

        if (node.Expression is not null)
        {
            var parsed = RuleExpression.Parse(node.Expression);
            if (parsed.Error is { } error)
            {
                errors.Add($"{label}: espressione personalizzata non valida, {error.Describe(node.Expression)}");
            }
        }
        else if (!isLeaf && !isGroup)
        {
            errors.Add($"{label}: condizione vuota.");
            return;
        }

        foreach (var (group, name) in new[] { (node.All, "all"), (node.Any, "any") })
        {
            if (group is null)
            {
                continue;
            }

            if (group.Count == 0)
            {
                errors.Add($"{label}: il gruppo '{name}' è vuoto.");
            }

            foreach (var child in group)
            {
                ValidateCondition(child, label, errors);
            }
        }

        if (node.Not is not null)
        {
            ValidateCondition(node.Not, label, errors);
        }

        if (!isLeaf)
        {
            return;
        }

        if (node.Field is null || !RuleFields.IsKnown(node.Field))
        {
            errors.Add($"{label}: campo sconosciuto '{node.Field}'.");
        }

        if (node.Op is null || !RuleEngine.Operators.Contains(node.Op))
        {
            errors.Add($"{label}: operatore sconosciuto '{node.Op}'.");
            return;
        }

        var op = node.Op.ToLowerInvariant();
        if (op is "isnull" or "notnull")
        {
            return;
        }

        if (node.Value is null)
        {
            errors.Add($"{label}: l'operatore '{node.Op}' richiede un valore.");
        }
        else if (op is "in" or "notin" && node.Value is not JsonArray)
        {
            errors.Add($"{label}: l'operatore '{node.Op}' richiede un elenco di valori.");
        }
        else if (op == "regex" && node.Value is JsonValue pattern && pattern.TryGetValue<string>(out var text))
        {
            try
            {
                _ = System.Text.RegularExpressions.Regex.IsMatch("", text);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"{label}: espressione regolare non valida ({ex.Message}).");
            }
        }
    }

    private static int ReadVersion(JsonNode node) =>
        node is JsonObject obj && obj["version"] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;

    private static JsonObject SetVersion(JsonObject obj, int version)
    {
        obj["version"] = version;
        return obj;
    }

    private static RulesLoadResult Failed(string error) => new(null, [error]);
}
