using System.Globalization;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Rules;

namespace LiveHelpDesk.Core.Notifications;

/// <param name="Key">What goes between the braces: <c>{{numero}}</c>.</param>
/// <param name="Sample">Example value, for the editor's preview.</param>
public sealed record TemplateField(string Key, string Label, string Description, string Sample);

/// <summary>The fields a notification text can show, and their values for an event.</summary>
public static class TemplateFields
{
    public const string Link = "link";

    public static IReadOnlyList<TemplateField> All { get; } =
    [
        new("numero", "Numero del ticket", "Il numero del ticket, es. CAS-01234.", "CAS-01234-X1Y2Z3"),
        new("titolo", "Titolo del ticket", "Il titolo del ticket.", "La stampa delle fatture non parte"),
        new("priorita", "Priorità", "La priorità del ticket (Alta, Normale, Bassa).", "Alta"),
        new("stato", "Stato", "Lo stato o livello del ticket, es. L1 In risoluzione.", "L1 In risoluzione"),
        new("livello", "Livello di supporto", "Il livello di supporto: L0, L1 o L2.", "L1"),
        new("evento", "Descrizione dell'evento", "Il titolo standard della notifica, es. «Nuova e-mail su #CAS-01234».", "Nuova e-mail su #CAS-01234-X1Y2Z3"),
        new("testo_standard", "Testo standard", "Il testo che LiveHelpDesk userebbe senza personalizzazione.", "La stampa delle fatture non parte\nPriorità: Alta · Stato: L1 In risoluzione\nDa: mario.rossi@cliente.it — Re: stampa fatture"),
        new("dettaglio", "Dettaglio dell'evento", "Mittente e oggetto, passaggio di stato o motivo del «senza risposta».", "Da: mario.rossi@cliente.it — Re: stampa fatture"),
        new("mittente", "Mittente del messaggio", "Chi ha scritto il messaggio (eventi sui messaggi).", "mario.rossi@cliente.it"),
        new("oggetto", "Oggetto del messaggio", "L'oggetto del messaggio (eventi sui messaggi).", "Re: stampa fatture"),
        new("tipo_messaggio", "Tipo di messaggio", "E-mail, Nota, Commento o Risposta.", "E-mail"),
        new("data_evento", "Data dell'evento", "Quando è successo, es. 31/12/2025 14:30.", "31/12/2025 14:30"),
        new("creato_il", "Data di creazione", "Quando è stato aperto il ticket.", "30/12/2025 09:15"),
        new("modificato_il", "Ultima modifica", "L'ultima modifica del ticket.", "31/12/2025 14:30"),
        new("eta_ticket", "Età del ticket", "Da quanto è aperto il ticket, es. 1 g 5 h.", "1 g 5 h"),
        new("in_sospeso", "In sospeso", "Sì se il ticket è in sospeso.", "No"),
        new("sollecito", "Con sollecito", "Sì se il ticket ha un sollecito.", "Sì"),
        new(Link, "Link al ticket", "L'indirizzo del ticket in Dynamics. Usalo così: [Apri il ticket]({{link}}).", "https://helpdesk.crm4.dynamics.com/main.aspx?pagetype=entityrecord&etn=incident"),
        new("regola", "Nome della regola", "La regola che ha generato la notifica.", "Le mie e-mail"),
        new("urgenza", "Urgenza", "L'urgenza scelta nella regola: Informazione, Attenzione o Critico.", "Attenzione"),
        new("applicativo", "Applicativo (codice)", "Il codice dell'applicativo del ticket.", "100000001"),
        new("tipologia", "Tipologia (codice)", "Il codice della tipologia del ticket.", "100000002"),
        new("businessunit", "Business unit (codice)", "Il codice della business unit del ticket.", "100000003"),
        new("classificazione", "Classificazione (codice)", "Il codice della classificazione del ticket.", "100000004"),
    ];

    private static readonly HashSet<string> Keys = new(All.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ExtraKeys = ["applicativo", "tipologia", "businessunit", "classificazione"];

    public static bool IsKnown(string key) => Keys.Contains(key);

    public static IReadOnlyDictionary<string, string?> Samples { get; } =
        All.ToDictionary(f => f.Key, f => (string?)f.Sample, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string?> Values(TicketEvent evt, TicketMapping mapping, string ruleName, Severity severity, Uri? link)
    {
        var ticket = evt.Ticket;
        var message = evt.Message;
        var (standardTitle, standardBody) = NotificationText.Describe(evt, mapping, DetailLevel.Full);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["numero"] = ticket?.Number ?? evt.TicketNumber,
            ["titolo"] = ticket?.Title ?? evt.Title,
            ["priorita"] = ticket is null ? null : mapping.PriorityLabels.GetValueOrDefault(ticket.Priority, ticket.Priority.ToString(CultureInfo.InvariantCulture)),
            ["stato"] = ticket is null ? null : mapping.StatusLabels.GetValueOrDefault(ticket.Status, ticket.Status.ToString(CultureInfo.InvariantCulture)),
            ["livello"] = ticket is not null && mapping.StatusLevels.TryGetValue(ticket.Status, out var level) ? $"L{level}" : null,
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

        foreach (var key in ExtraKeys)
        {
            values[key] = ticket?.Extra.GetValueOrDefault(key);
        }

        return values;
    }

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
