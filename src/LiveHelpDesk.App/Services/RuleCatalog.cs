using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.Services;

/// <summary>A value offered by an editor. Hidden choices are not in use and only appear where they are already selected.</summary>
public sealed record Choice(string Value, string Label, bool Hidden = false)
{
    public override string ToString() => Label;
}

public enum FieldKind
{
    Text,
    Number,
    Choice,
    YesNo,
    Lookup,
}

public sealed record FieldInfo(string Key, string Label, FieldKind Kind, IReadOnlyList<Choice> Choices, bool? NumericOverride = null)
{
    public override string ToString() => Label;

    /// <summary>Fields whose values are numbers in the rules file.</summary>
    public bool IsNumeric => NumericOverride ?? (Kind == FieldKind.Number || Key.ToLowerInvariant() is "priority" or "status" or "level" or "state" or "commstatus");
}

/// <summary>What the rule editor offers, in the user's words: which fields, which operators, which events and channels.</summary>
public static class RuleCatalog
{
    public static readonly IReadOnlyList<Choice> YesNo = [new("true", "Sì"), new("false", "No")];

    public static IReadOnlyList<Choice> Events { get; } =
    [
        new(nameof(TicketEventType.TicketCreated), "Nuovo ticket"),
        new(nameof(TicketEventType.MessageReceived), "Nuovo messaggio del cliente"),
        new(nameof(TicketEventType.CustomerReplied), "Il cliente ha risposto"),
        new(nameof(TicketEventType.ReplySent), "Risposta inviata da noi"),
        new(nameof(TicketEventType.TicketAssigned), "Ticket assegnato"),
        new(nameof(TicketEventType.TicketReassigned), "Ticket riassegnato"),
        new(nameof(TicketEventType.QueueChanged), "Cambio di coda"),
        new(nameof(TicketEventType.Level2AssigneeChanged), "Cambio dell'utente di livello 2"),
        new(nameof(TicketEventType.StatusChanged), "Cambio di stato o livello"),
        new(nameof(TicketEventType.PriorityChanged), "Cambio di priorità"),
        new(nameof(TicketEventType.ReminderFlagged), "Sollecito"),
        new(nameof(TicketEventType.Unattended), "Ticket senza risposta"),
        new(nameof(TicketEventType.MonitorHealth), "Problemi del monitoraggio"),
        new(nameof(TicketEventType.SlaWarning), "SLA in scadenza (non ancora attivo)", Hidden: true),
        new(nameof(TicketEventType.SlaBreached), "SLA scaduto (non ancora attivo)", Hidden: true),
    ];

    public static IReadOnlyList<Choice> Channels { get; } =
    [
        new("toast", "Notifica di Windows"),
        new("overlay", "Finestrella lampeggiante"),
        new("email", "E-mail"),
        new("teams", "Teams"),
        new("sms", "SMS", Hidden: true),
        new("whatsapp", "WhatsApp", Hidden: true),
    ];

    /// <summary>The choices an editor shows: the ones in use, plus hidden ones already selected so saving does not drop them.</summary>
    public static IEnumerable<Choice> Offered(IReadOnlyList<Choice> choices, Func<string, bool> isSelected) =>
        choices.Where(c => !c.Hidden || isSelected(c.Value));

    public static IReadOnlyList<Choice> Severities { get; } =
    [
        new(nameof(Severity.Info), "Informazione"),
        new(nameof(Severity.Attention), "Attenzione"),
        new(nameof(Severity.Critical), "Critico"),
    ];

    public static IReadOnlyList<Choice> Details { get; } =
    [
        new("Full", "Completo"),
        new("Minimal", "Minimo (solo numero e priorità)"),
    ];

    public static string Label(IReadOnlyList<Choice> choices, string value) =>
        choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;

    /// <summary>The fields a condition can look at. Queues, owners and users are offered by name when the service could look them up.</summary>
    public static IReadOnlyList<FieldInfo> Fields(TicketMapping mapping, IReadOnlyList<LookupItem> lookups)
    {
        Choice[] Named(params string[] kinds) =>
            [.. lookups.Where(l => kinds.Contains(l.Kind)).Select(l => new Choice(l.Id, l.Name)).OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)];

        return
        [
            new("priority", "Priorità", FieldKind.Choice, [.. mapping.PriorityLabels.OrderBy(p => p.Key).Select(p => new Choice(p.Key.ToString(), p.Value))]),
            new("status", "Stato o livello", FieldKind.Choice, [.. mapping.StatusLabels.OrderBy(p => p.Key).Select(p => new Choice(p.Key.ToString(), p.Value))]),
            new("level", "Livello di supporto", FieldKind.Choice, [new("0", "L0"), new("1", "L1"), new("2", "L2")]),
            new("state", "Stato generale", FieldKind.Choice, [new("0", "Attivo"), new("1", "Risolto"), new("2", "Annullato")]),
            new("owner", "Assegnato a", FieldKind.Lookup, [new("$me", "Io"), .. Named("user", "team", "queue")]),
            new("queue", "Coda", FieldKind.Lookup, Named("queue", "team")),
            new("level2user", "Utente di livello 2", FieldKind.Lookup, Named("user")),
            new("number", "Numero del ticket", FieldKind.Text, []),
            new("title", "Titolo del ticket", FieldKind.Text, []),
            new("message.from", "Mittente del messaggio", FieldKind.Text, []),
            new("message.subject", "Oggetto del messaggio", FieldKind.Text, []),
            new("message.kind", "Tipo di messaggio", FieldKind.Choice, [new("Email", "E-mail"), new("Note", "Nota"), new("Comment", "Commento"), new("Reply", "Risposta")]),
            new("ageMinutes", "Età del ticket (minuti)", FieldKind.Number, []),
            new("onHold", "In sospeso", FieldKind.YesNo, YesNo),
            new("reminder", "Con sollecito", FieldKind.YesNo, YesNo),
            new("subtype", "Dettaglio dell'evento (codice)", FieldKind.Text, []),
            new("extra.applicativo", "Applicativo (codice)", FieldKind.Text, []),
            new("extra.tipologia", "Tipologia (codice)", FieldKind.Text, []),
            new("extra.businessunit", "Business unit (codice)", FieldKind.Text, []),
            new("extra.classificazione", "Classificazione (codice)", FieldKind.Text, []),
        ];
    }

    /// <summary>A field the editor has no name for (typed by hand in the JSON): kept working, shown by its raw name.</summary>
    public static FieldInfo Custom(string key, bool numeric = false) => new(key, $"Campo: {key}", FieldKind.Text, [], numeric);

    public static IReadOnlyList<Choice> OperatorsFor(FieldKind kind) => kind switch
    {
        FieldKind.Number => [new("gt", "è maggiore di"), new("lt", "è minore di"), new("gte", "è almeno"), new("lte", "è al massimo"), new("eq", "è uguale a")],
        FieldKind.Choice => [new("eq", "è"), new("ne", "non è"), new("in", "è uno tra (separati da virgola)"), new("notIn", "non è nessuno tra")],
        FieldKind.YesNo => [new("eq", "è")],
        FieldKind.Lookup => [new("eq", "è"), new("ne", "non è"), new("isNull", "non è indicato / non assegnato"), new("notNull", "è indicato / assegnato")],
        _ => [new("contains", "contiene"), new("startsWith", "inizia con"), new("eq", "è esattamente"), new("ne", "non è"), new("in", "è uno tra (separati da virgola)"), new("notIn", "non è nessuno tra"), new("regex", "corrisponde all'espressione"), new("isNull", "è vuoto"), new("notNull", "non è vuoto")],
    };

    public static bool NeedsValue(string op) => !op.Equals("isNull", StringComparison.OrdinalIgnoreCase) && !op.Equals("notNull", StringComparison.OrdinalIgnoreCase);
}
