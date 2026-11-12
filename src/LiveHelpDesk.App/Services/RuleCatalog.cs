using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Rules;
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

    /// <summary>How a custom expression writes the field (<c>priorita</c>).</summary>
    public string Name { get; init; } = Key;

    public string Description { get; init; } = "";

    public string Group { get; init; } = "";

    /// <summary>True when an expression can compare the field by name ("Alta") as well as by code.</summary>
    public bool HasNames { get; init; }

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

    /// <summary>
    /// The fields a condition can look at (all those of <see cref="RuleFields"/>), with the values they admit.
    /// Queues, owners and users are offered by name when the service could look them up.
    /// </summary>
    public static IReadOnlyList<FieldInfo> Fields(TicketMapping mapping, IReadOnlyList<LookupItem> lookups)
    {
        Choice[] Named(params string[] kinds) =>
            [.. lookups.Where(l => kinds.Contains(l.Kind)).Select(l => new Choice(l.Id, l.Name)).OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)];

        IReadOnlyList<Choice> ChoicesOf(string key) => key.ToLowerInvariant() switch
        {
            "priority" => [.. mapping.PriorityLabels.OrderBy(p => p.Key).Select(p => new Choice(p.Key.ToString(), p.Value))],
            "status" => [.. mapping.StatusLabels.OrderBy(p => p.Key).Select(p => new Choice(p.Key.ToString(), p.Value))],
            "level" => [new("0", "L0"), new("1", "L1"), new("2", "L2")],
            "state" => [new(mapping.ActiveState.ToString(), "Attivo"), new(mapping.ResolvedState.ToString(), "Risolto"), new(mapping.CancelledState.ToString(), "Annullato")],
            "owner" => [new("$me", "Io"), .. Named("user", "team", "queue")],
            "queue" => Named("queue", "team"),
            "level2user" => [new("$me", "Io"), .. Named("user")],
            "message.kind" => [new("Email", "E-mail"), new("Note", "Nota"), new("Comment", "Commento"), new("Reply", "Risposta")],
            "eventtype" => [.. Events.Where(e => !e.Hidden)],
            _ => [],
        };

        return
        [
            .. RuleFields.All.Select(f => new FieldInfo(f.Key, f.Label, KindOf(f.Kind), f.Kind == RuleFieldKind.YesNo ? YesNo : ChoicesOf(f.Key))
            {
                Name = f.Name,
                Description = f.Description,
                Group = f.Group,
                HasNames = f.HasNames,
            }),
        ];
    }

    private static FieldKind KindOf(RuleFieldKind kind) => kind switch
    {
        RuleFieldKind.Number => FieldKind.Number,
        RuleFieldKind.Choice => FieldKind.Choice,
        RuleFieldKind.YesNo => FieldKind.YesNo,
        RuleFieldKind.Lookup => FieldKind.Lookup,
        _ => FieldKind.Text,
    };

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
