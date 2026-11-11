using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LiveHelpDesk.Core.Notifications;

/// <param name="File">The parsed templates; null when the text could not be read at all.</param>
/// <param name="Errors">Human-readable problems; a file with errors must not replace the last good one.</param>
public sealed record TemplatesLoadResult(TemplatesFile? File, IReadOnlyList<string> Errors)
{
    public bool IsValid => File is not null && Errors.Count == 0;
}

/// <summary>Reading, validating and writing the versioned notification texts file.</summary>
public static class TemplatesConfig
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TemplatesLoadResult Parse(string json)
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

        var version = node is JsonObject obj && obj["version"] is JsonValue value && value.TryGetValue<int>(out var v) ? v : CurrentVersion;
        if (version > CurrentVersion)
        {
            return Failed($"Il file è della versione {version}, più recente di quella supportata ({CurrentVersion}).");
        }

        TemplatesFile? file;
        try
        {
            file = node.Deserialize<TemplatesFile>(Options);
        }
        catch (JsonException ex)
        {
            return Failed($"Struttura dei testi di notifica non valida: {ex.Message}");
        }

        return file is null ? Failed("Il file è vuoto.") : new TemplatesLoadResult(file, Validate(file));
    }

    public static string Serialize(TemplatesFile file) => JsonSerializer.Serialize(file, Options);

    public static IReadOnlyList<string> Validate(TemplatesFile file)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<(string Rule, string Channel), string>();

        foreach (var (template, index) in file.Templates.Select((t, i) => (t, i)))
        {
            var label = !string.IsNullOrWhiteSpace(template.Name) ? $"testo '{template.Name}'"
                : string.IsNullOrWhiteSpace(template.Id) ? $"testo #{index + 1}" : $"testo '{template.Id}'";

            if (string.IsNullOrWhiteSpace(template.Id))
            {
                errors.Add($"{label}: manca l'id.");
            }
            else if (!seen.Add(template.Id))
            {
                errors.Add($"{label}: id duplicato.");
            }

            if (template.RuleIds.Count == 0)
            {
                errors.Add($"{label}: nessuna regola selezionata.");
            }

            if (template.Channels.Count == 0)
            {
                errors.Add($"{label}: nessun canale selezionato.");
            }

            if (string.IsNullOrWhiteSpace(template.Title) && string.IsNullOrWhiteSpace(template.Body))
            {
                errors.Add($"{label}: il titolo e il testo sono vuoti.");
            }

            if (template.Title.Contains('\n'))
            {
                errors.Add($"{label}: il titolo deve stare su una riga.");
            }

            foreach (var problem in TemplateRenderer.CheckTags(template.Title).Concat(TemplateRenderer.CheckTags(template.Body)).Distinct())
            {
                errors.Add($"{label}: {problem}");
            }

            foreach (var rule in template.RuleIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var channel in template.Channels.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var key = (rule.ToLowerInvariant(), channel.ToLowerInvariant());
                    if (owners.TryGetValue(key, out var other))
                    {
                        errors.Add($"{label}: la regola '{rule}' sul canale '{channel}' ha già il {other}.");
                    }
                    else
                    {
                        owners[key] = label;
                    }
                }
            }
        }

        return errors;
    }

    private static TemplatesLoadResult Failed(string error) => new(null, [error]);
}
