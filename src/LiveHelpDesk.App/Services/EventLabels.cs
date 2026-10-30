namespace LiveHelpDesk.App.Services;

/// <summary>How an event is called on screen. Short Italian phrases; unknown ones fall back to their raw name.</summary>
public static class EventLabels
{
    public static string Describe(string type, string? subtype) => (type, subtype) switch
    {
        ("TicketCreated", _) => "Nuovo ticket",
        ("MessageReceived", "Email") => "Nuova e-mail",
        ("MessageReceived", "Note") => "Nuova nota",
        ("MessageReceived", "Comment") => "Nuovo commento",
        ("MessageReceived", _) => "Nuovo messaggio",
        ("CustomerReplied", _) => "Il cliente ha risposto",
        ("ReplySent", _) => "Risposta inviata",
        ("TicketAssigned", _) => "Assegnato",
        ("TicketReassigned", "Unassigned") => "Senza assegnatario",
        ("TicketReassigned", _) => "Riassegnato",
        ("QueueChanged", _) => "Cambio coda",
        ("Level2AssigneeChanged", _) => "Cambio utente di livello 2",
        ("StatusChanged", "Reopened") => "Riaperto",
        ("StatusChanged", "Resolved") => "Risolto",
        ("StatusChanged", "Closed") => "Chiuso",
        ("StatusChanged", "Cancelled") => "Annullato",
        ("StatusChanged", "Merged") => "Unito",
        ("StatusChanged", "Disappeared") => "Non più visibile",
        ("StatusChanged", "LevelChanged") => "Passato di livello",
        ("StatusChanged", _) => "Cambio di stato",
        ("PriorityChanged", "Increased") => "Priorità aumentata",
        ("PriorityChanged", _) => "Priorità ridotta",
        ("ReminderFlagged", _) => "Sollecito",
        ("Unattended", _) => "Senza risposta",
        ("SlaWarning", _) => "SLA in scadenza",
        ("SlaBreached", _) => "SLA scaduto",
        ("MonitorHealth", "AuthExpired") => "Accesso scaduto",
        ("MonitorHealth", "BaselineCompleted") => "Monitoraggio avviato",
        ("MonitorHealth", "Recovered" or "ChannelRecovered") => "Ripristinato",
        ("MonitorHealth", "ChannelFailing") => "Canale non funzionante",
        ("MonitorHealth", _) => "Stato del monitoraggio",
        _ => subtype is null ? type : $"{type} / {subtype}",
    };
}
