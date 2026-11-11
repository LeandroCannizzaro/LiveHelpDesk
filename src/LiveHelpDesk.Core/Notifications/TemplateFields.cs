using System.Globalization;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Rules;

namespace LiveHelpDesk.Core.Notifications;

/// <param name="Key">What goes between the braces: <c>{{numero}}</c>.</param>
/// <param name="Sample">Example value, for the editor's preview.</param>
/// <param name="Group">The heading the field is listed under in the editor.</param>
public sealed record TemplateField(string Key, string Label, string Description, string Sample, string Group = "");

/// <summary>The fields a notification text can show, and their values for an event.</summary>
public static class TemplateFields
{
    public const string Link = "link";

    public const string TicketGroup = "Ticket";
    public const string PeopleGroup = "Cliente e persone";
    public const string ClassificationGroup = "Classificazione";
    public const string EventGroup = "Evento";
    public const string RuleGroup = "Regola";

    /// <summary>The installation's extra columns, shown both by name (<c>{{applicativo_nome}}</c>) and by code (<c>{{applicativo}}</c>).</summary>
    private static readonly (string Key, string Label, string SampleName, string SampleCode)[] ExtraFields =
    [
        ("applicativo", "Applicativo", "Fatturazione elettronica", "100000001"),
        ("tipologia", "Tipologia", "Anomalia", "100000002"),
        ("businessunit", "Business unit", "Gestionali", "100000003"),
        ("classificazione", "Classificazione", "Software", "100000004"),
        ("prioritariaqualificata", "Priorità qualificata", "Alta", "100000005"),
    ];

    public static IReadOnlyList<TemplateField> All { get; } =
    [
        new("numero", "Numero del ticket", "Il numero del ticket, es. CAS-01234.", "CAS-01234-X1Y2Z3", TicketGroup),
        new("titolo", "Titolo del ticket", "Il titolo del ticket.", "La stampa delle fatture non parte", TicketGroup),
        new("priorita", "Priorità", "La priorità del ticket (Alta, Normale, Bassa).", "Alta", TicketGroup),
        new("stato", "Stato", "Lo stato o livello del ticket, es. L1 In risoluzione.", "L1 In risoluzione", TicketGroup),
        new("stato_ticket", "Stato del record", "Attivo, Risolto o Annullato.", "Attivo", TicketGroup),
        new("livello", "Livello di supporto", "Il livello di supporto: L0, L1 o L2.", "L1", TicketGroup),
        new("stato_comunicazioni", "Stato comunicazioni", "Lo stato delle comunicazioni del ticket, es. Risposta ricevuta.", "Risposta ricevuta", TicketGroup),
        new("in_sospeso", "In sospeso", "Sì se il ticket è in sospeso.", "No", TicketGroup),
        new("sollecito", "Con sollecito", "Sì se il ticket ha un sollecito.", "Sì", TicketGroup),
        new("creato_il", "Data di creazione", "Quando è stato aperto il ticket.", "30/12/2025 09:15", TicketGroup),
        new("modificato_il", "Ultima modifica", "L'ultima modifica del ticket.", "31/12/2025 14:30", TicketGroup),
        new("eta_ticket", "Età del ticket", "Da quanto è aperto il ticket, es. 1 g 5 h.", "1 g 5 h", TicketGroup),
        new(Link, "Link al ticket", "L'indirizzo del ticket in Dynamics. Usalo così: [Apri il ticket]({{link}}).", "https://helpdesk.crm4.dynamics.com/main.aspx?pagetype=entityrecord&etn=incident", TicketGroup),

        new("cliente", "Cliente", "Il nome del cliente del ticket.", "Rossi Forniture S.r.l.", PeopleGroup),
        new("proprietario", "Proprietario", "Chi ha in carico il ticket (utente o team).", "Luca Bianchi", PeopleGroup),
        new("coda", "Coda / team", "La coda o il team a cui è assegnato il ticket.", "Supporto L1", PeopleGroup),
        new("utente_l2", "Utente di 2° livello", "L'utente di secondo livello assegnato.", "Anna Verdi", PeopleGroup),
        new("creato_da", "Creato da", "Chi ha aperto il ticket in Dynamics.", "Luca Bianchi", PeopleGroup),
        new("modificato_da", "Modificato da", "Chi ha fatto l'ultima modifica.", "Anna Verdi", PeopleGroup),

        .. ExtraFields.SelectMany(f => new TemplateField[]
        {
            new($"{f.Key}_nome", f.Label, $"{f.Label} del ticket, come la mostra Dynamics.", f.SampleName, ClassificationGroup),
            new(f.Key, $"{f.Label} (codice)", $"Il codice di {f.Label.ToLowerInvariant()} del ticket.", f.SampleCode, ClassificationGroup),
        }),

        new("evento", "Descrizione dell'evento", "Il titolo standard della notifica, es. «Nuova e-mail su #CAS-01234».", "Nuova e-mail su #CAS-01234-X1Y2Z3", EventGroup),
        new("testo_standard", "Testo standard", "Il testo che LiveHelpDesk userebbe senza personalizzazione.", "La stampa delle fatture non parte\nPriorità: Alta · Stato: L1 In risoluzione\nDa: mario.rossi@cliente.it — Re: stampa fatture", EventGroup),
        new("dettaglio", "Dettaglio dell'evento", "Mittente e oggetto, passaggio di stato o motivo del «senza risposta».", "Da: mario.rossi@cliente.it — Re: stampa fatture", EventGroup),
        new("mittente", "Mittente del messaggio", "Chi ha scritto il messaggio (eventi sui messaggi).", "mario.rossi@cliente.it", EventGroup),
        new("oggetto", "Oggetto del messaggio", "L'oggetto del messaggio (eventi sui messaggi).", "Re: stampa fatture", EventGroup),
        new("tipo_messaggio", "Tipo di messaggio", "E-mail, Nota, Commento o Risposta.", "E-mail", EventGroup),
        new("direzione", "Direzione del messaggio", "Ricevuto o Inviato (eventi sui messaggi).", "Ricevuto", EventGroup),
        new("data_evento", "Data dell'evento", "Quando è successo, es. 31/12/2025 14:30.", "31/12/2025 14:30", EventGroup),

        new("regola", "Nome della regola", "La regola che ha generato la notifica.", "Le mie e-mail", RuleGroup),
        new("urgenza", "Urgenza", "L'urgenza scelta nella regola: Informazione, Attenzione o Critico.", "Attenzione", RuleGroup),
    ];

    private static readonly HashSet<string> Keys = new(All.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string key) => Keys.Contains(key);

    public static IReadOnlyDictionary<string, string?> Samples { get; } =
        All.ToDictionary(f => f.Key, f => (string?)f.Sample, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string?> Values(TicketEvent evt, TicketMapping mapping, string ruleName, Severity severity, Uri? link)
    {
        var ticket = evt.Ticket;
        var message = evt.Message;
        var (standardTitle, standardBody) = NotificationText.Describe(evt, mapping, DetailLevel.Full);
        string? Label(string role) => ticket?.Labels.GetValueOrDefault(role) is { Length: > 0 } text ? text : null;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["numero"] = ticket?.Number ?? evt.TicketNumber,
            ["titolo"] = ticket?.Title ?? evt.Title,
            ["priorita"] = ticket is null ? null : mapping.PriorityLabels.GetValueOrDefault(ticket.Priority) ?? Label(TicketLabels.Priority) ?? ticket.Priority.ToString(CultureInfo.InvariantCulture),
            ["stato"] = ticket is null ? null : mapping.StatusLabels.GetValueOrDefault(ticket.Status) ?? Label(TicketLabels.Status) ?? ticket.Status.ToString(CultureInfo.InvariantCulture),
            ["stato_ticket"] = ticket is null ? null : Label(TicketLabels.State) ?? RecordState(ticket.State, mapping),
            ["livello"] = ticket is not null && mapping.StatusLevels.TryGetValue(ticket.Status, out var level) ? $"L{level}" : null,
            ["stato_comunicazioni"] = Label(TicketLabels.CommunicationStatus) ?? ticket?.CommunicationStatus?.ToString(CultureInfo.InvariantCulture),
            ["cliente"] = Label(TicketLabels.Customer),
            ["proprietario"] = Label(TicketLabels.Owner),
            ["coda"] = Label(TicketLabels.Queue),
            ["utente_l2"] = Label(TicketLabels.Level2User),
            ["creato_da"] = Label(TicketLabels.CreatedBy),
            ["modificato_da"] = Label(TicketLabels.ModifiedBy),
            ["evento"] = standardTitle,
            ["testo_standard"] = standardBody,
            ["dettaglio"] = NotificationText.Detail(evt, mapping),
            ["mittente"] = message?.From,
            ["oggetto"] = message?.Subject,
            ["tipo_messaggio"] = message?.Kind switch
            {
                MessageKind.Email => "E-mail",
                MessageKind.Note => "Nota",
                MessageKind.Comment => "Commento",
                MessageKind.Reply => "Risposta",
                _ => null,
            },
            ["direzione"] = message is null ? null : message.Incoming ? "Ricevuto" : "Inviato",
            ["data_evento"] = Date(evt.OccurredAt),
            ["creato_il"] = ticket is null ? null : Date(ticket.CreatedOn),
            ["modificato_il"] = ticket is null ? null : Date(ticket.ModifiedOn),
            ["eta_ticket"] = ticket is null ? null : Age(evt.OccurredAt - ticket.CreatedOn),
            ["in_sospeso"] = ticket is null ? null : YesNo(ticket.OnHold),
            ["sollecito"] = ticket is null ? null : YesNo(ticket.Reminder),
            [Link] = link?.AbsoluteUri,
            ["regola"] = ruleName,
            ["urgenza"] = severity switch
            {
                Severity.Critical => "Critico",
                Severity.Attention => "Attenzione",
                _ => "Informazione",
            },
        };

        foreach (var (key, _, _, _) in ExtraFields)
        {
            var code = ticket?.Extra.GetValueOrDefault(key);
            values[key] = code;
            values[$"{key}_nome"] = Label(key) ?? code;
        }

        return values;
    }

    private static string? RecordState(int state, TicketMapping mapping) =>
        state == mapping.ActiveState ? "Attivo"
        : state == mapping.ResolvedState ? "Risolto"
        : state == mapping.CancelledState ? "Annullato"
        : state.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "Sì" : "No";

    private static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalDays >= 1 ? $"{(int)age.TotalDays} g {age.Hours} h"
            : age.TotalHours >= 1 ? $"{(int)age.TotalHours} h {age.Minutes} min"
            : $"{(int)age.TotalMinutes} min";
    }
}
