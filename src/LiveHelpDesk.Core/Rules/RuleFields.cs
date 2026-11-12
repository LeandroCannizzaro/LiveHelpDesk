using System.Globalization;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

public enum RuleFieldKind
{
    Text,
    Number,
    Choice,
    YesNo,
    Lookup,
}

/// <summary>A field a rule can look at.</summary>
/// <param name="Key">The name in the rules file (<c>"field": "priority"</c>).</param>
/// <param name="Name">The name in a custom expression (<c>priorita = "Alta"</c>), like the fields of the notification texts.</param>
/// <param name="Group">The heading the field is listed under in the editor.</param>
public sealed record RuleField(string Key, string Name, string Label, string Description, RuleFieldKind Kind, string Group)
{
    internal Func<TicketEvent, EvaluationContext, object?> Read { get; init; } = (_, _) => null;

    /// <summary>The name Dynamics (or the mapping) gives the value, when it has one: "Alta" for priority 1.</summary>
    internal Func<TicketEvent, EvaluationContext, string?>? ReadName { get; init; }

    /// <summary>True when a value can be written by name ("Alta", "Supporto L1") as well as by code.</summary>
    public bool HasNames => ReadName is not null;
}

/// <summary>The fields a rule condition can look at, read from an event and the ticket it carries.</summary>
public static class RuleFields
{
    public const string TicketGroup = "Ticket";
    public const string PeopleGroup = "Cliente e persone";
    public const string ClassificationGroup = "Classificazione";
    public const string EventGroup = "Evento";

    private const string ExtraPrefix = "extra.";

    /// <summary>The installation's extra columns (mapping roles), with their names in the notification texts.</summary>
    private static readonly (string Role, string Label)[] ExtraFields =
    [
        ("applicativo", "Applicativo"),
        ("tipologia", "Tipologia"),
        ("businessunit", "Business unit"),
        ("classificazione", "Classificazione"),
        ("prioritariaqualificata", "Priorità qualificata"),
    ];

    public static IReadOnlyList<RuleField> All { get; } =
    [
        new("priority", "priorita", "Priorità", "La priorità del ticket: per nome (\"Alta\") o per codice (1).", RuleFieldKind.Choice, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.Priority,
            ReadName = (e, c) => e.Ticket is { } t ? c.Mapping.PriorityLabels.GetValueOrDefault(t.Priority) ?? Label(e, TicketLabels.Priority) : null,
        },
        new("status", "stato", "Stato o livello", "Lo stato del ticket, es. \"L1 In risoluzione\" (o il suo codice).", RuleFieldKind.Choice, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.Status,
            ReadName = (e, c) => e.Ticket is { } t ? c.Mapping.StatusLabels.GetValueOrDefault(t.Status) ?? Label(e, TicketLabels.Status) : null,
        },
        new("state", "stato_ticket", "Stato generale", "Attivo, Risolto o Annullato (codici 0, 1, 2).", RuleFieldKind.Choice, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.State,
            ReadName = (e, c) => e.Ticket is { } t ? RecordState(t.State, c.Mapping) ?? Label(e, TicketLabels.State) : null,
        },
        new("level", "livello", "Livello di supporto", "Il livello di supporto: \"L0\", \"L1\" o \"L2\" (codici 0, 1, 2).", RuleFieldKind.Choice, TicketGroup)
        {
            Read = (e, c) => e.Ticket is { } t && c.Mapping.StatusLevels.TryGetValue(t.Status, out var level) ? level : null,
            ReadName = (e, c) => e.Ticket is { } t && c.Mapping.StatusLevels.TryGetValue(t.Status, out var level) ? $"L{level}" : null,
        },
        new("number", "numero", "Numero del ticket", "Il numero del ticket, es. CAS-01234.", RuleFieldKind.Text, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.Number ?? e.TicketNumber,
        },
        new("title", "titolo", "Titolo del ticket", "Il titolo del ticket.", RuleFieldKind.Text, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.Title ?? e.Title,
        },
        new("commStatus", "stato_comunicazioni", "Stato comunicazioni", "Lo stato delle comunicazioni, es. \"Risposta ricevuta\" (o il suo codice).", RuleFieldKind.Text, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.CommunicationStatus,
            ReadName = (e, _) => Label(e, TicketLabels.CommunicationStatus),
        },
        new("onHold", "in_sospeso", "In sospeso", "Sì se il ticket è in sospeso.", RuleFieldKind.YesNo, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.OnHold,
        },
        new("reminder", "sollecito", "Con sollecito", "Sì se il ticket ha un sollecito.", RuleFieldKind.YesNo, TicketGroup)
        {
            Read = (e, _) => e.Ticket?.Reminder,
        },
        new("ageMinutes", "eta_minuti", "Età del ticket (minuti)", "Da quanti minuti è aperto il ticket.", RuleFieldKind.Number, TicketGroup)
        {
            Read = (e, c) => e.Ticket is { } t ? (c.Now - t.CreatedOn).TotalMinutes : null,
        },

        new("customer", "cliente", "Cliente", "Il cliente del ticket, per nome (es. cliente contiene \"Rossi\").", RuleFieldKind.Text, PeopleGroup)
        {
            Read = (e, _) => e.Ticket?.CustomerId,
            ReadName = (e, _) => Label(e, TicketLabels.Customer),
        },
        new("owner", "proprietario", "Assegnato a", "Chi ha in carico il ticket (utente, team o coda); $me sono io.", RuleFieldKind.Lookup, PeopleGroup)
        {
            Read = (e, _) => e.Ticket?.OwnerId,
            ReadName = (e, _) => Label(e, TicketLabels.Owner),
        },
        new("queue", "coda", "Coda", "La coda o il team a cui è assegnato il ticket.", RuleFieldKind.Lookup, PeopleGroup)
        {
            Read = (e, _) => e.Ticket?.QueueId,
            ReadName = (e, _) => Label(e, TicketLabels.Queue),
        },
        new("level2user", "utente_l2", "Utente di livello 2", "L'utente di secondo livello assegnato; $me sono io.", RuleFieldKind.Lookup, PeopleGroup)
        {
            Read = (e, _) => e.Ticket?.Level2UserId,
            ReadName = (e, _) => Label(e, TicketLabels.Level2User),
        },
        new("createdBy", "creato_da", "Creato da", "Chi ha aperto il ticket in Dynamics (nome).", RuleFieldKind.Text, PeopleGroup)
        {
            Read = (e, _) => Label(e, TicketLabels.CreatedBy),
        },
        new("modifiedBy", "modificato_da", "Modificato da", "Chi ha fatto l'ultima modifica (nome).", RuleFieldKind.Text, PeopleGroup)
        {
            Read = (e, _) => Label(e, TicketLabels.ModifiedBy),
        },

        .. ExtraFields.Select(f => new RuleField(
            ExtraPrefix + f.Role, f.Role, f.Label, $"{f.Label} del ticket, per nome o per codice.", RuleFieldKind.Text, ClassificationGroup)
        {
            Read = (e, _) => e.Ticket?.Extra.GetValueOrDefault(f.Role),
            ReadName = (e, _) => Label(e, f.Role),
        }),

        new("eventType", "tipo_evento", "Tipo di evento", "Il fatto che ha fatto scattare la regola, es. \"TicketCreated\".", RuleFieldKind.Choice, EventGroup)
        {
            Read = (e, _) => e.Type.ToString(),
        },
        new("subtype", "sottotipo", "Dettaglio dell'evento (codice)", "La classificazione fine dell'evento, es. \"Email\", \"LevelChanged\".", RuleFieldKind.Text, EventGroup)
        {
            Read = (e, _) => e.Subtype,
        },
        new("change", "cambiamento", "Passaggio (da->a)", "Il passaggio per gli eventi di cambio, come codici \"da->a\".", RuleFieldKind.Text, EventGroup)
        {
            Read = (e, _) => e.Change,
        },
        new("message.kind", "tipo_messaggio", "Tipo di messaggio", "E-mail, Nota, Commento o Risposta.", RuleFieldKind.Choice, EventGroup)
        {
            Read = (e, _) => e.Message?.Kind.ToString(),
            ReadName = (e, _) => e.Message?.Kind switch
            {
                MessageKind.Email => "E-mail",
                MessageKind.Note => "Nota",
                MessageKind.Comment => "Commento",
                MessageKind.Reply => "Risposta",
                _ => null,
            },
        },
        new("message.incoming", "messaggio_ricevuto", "Messaggio ricevuto", "Sì se il messaggio viene dal cliente, No se l'abbiamo inviato noi.", RuleFieldKind.YesNo, EventGroup)
        {
            Read = (e, _) => e.Message?.Incoming,
        },
        new("message.from", "mittente", "Mittente del messaggio", "Chi ha scritto il messaggio.", RuleFieldKind.Text, EventGroup)
        {
            Read = (e, _) => e.Message?.From,
        },
        new("message.subject", "oggetto", "Oggetto del messaggio", "L'oggetto del messaggio.", RuleFieldKind.Text, EventGroup)
        {
            Read = (e, _) => e.Message?.Subject,
        },
    ];

    private static readonly Dictionary<string, RuleField> ByKey = All.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, RuleField> ByName = All.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Names => ByKey.Keys;

    public static bool IsKnown(string field) =>
        ByKey.ContainsKey(field)
        || (field.StartsWith(ExtraPrefix, StringComparison.OrdinalIgnoreCase) && field.Length > ExtraPrefix.Length);

    public static RuleField? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>
    /// The field an expression means by <paramref name="identifier"/>: its expression name, its file key, or
    /// <c>extra.ruolo</c> for an installation column. Returns the file key, or null when nothing matches.
    /// </summary>
    public static string? Resolve(string identifier)
    {
        if (ByName.TryGetValue(identifier, out var byName))
        {
            return byName.Key;
        }

        if (ByKey.TryGetValue(identifier, out var byKey))
        {
            return byKey.Key;
        }

        return IsKnown(identifier) ? identifier : null;
    }

    /// <summary>How an expression writes the field with this file key.</summary>
    public static string ExpressionName(string key) => ByKey.TryGetValue(key, out var field) ? field.Name : key;

    /// <summary>The field as comparable text (numbers and booleans in invariant form), or null when absent.</summary>
    public static string? GetText(string field, TicketEvent evt, EvaluationContext context)
    {
        object? value;
        if (ByKey.TryGetValue(field, out var known))
        {
            value = known.Read(evt, context);
        }
        else if (field.StartsWith(ExtraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = evt.Ticket?.Extra.GetValueOrDefault(field[ExtraPrefix.Length..]);
        }
        else
        {
            value = null;
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

    /// <summary>The name shown for the field's value ("Alta", "Supporto L1"), or null when it has none.</summary>
    public static string? GetName(string field, TicketEvent evt, EvaluationContext context)
    {
        if (ByKey.TryGetValue(field, out var known))
        {
            return known.ReadName?.Invoke(evt, context);
        }

        return field.StartsWith(ExtraPrefix, StringComparison.OrdinalIgnoreCase) ? Label(evt, field[ExtraPrefix.Length..]) : null;
    }

    private static string? Label(TicketEvent evt, string role) =>
        evt.Ticket?.Labels.GetValueOrDefault(role) is { Length: > 0 } text ? text : null;

    private static string? RecordState(int state, TicketMapping mapping) =>
        state == mapping.ActiveState ? "Attivo"
        : state == mapping.ResolvedState ? "Risolto"
        : state == mapping.CancelledState ? "Annullato"
        : null;
}
