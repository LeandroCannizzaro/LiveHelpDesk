using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Rules;

namespace LiveHelpDesk.Core.Notifications;

/// <summary>The words of a notification. Italian, short, and different for channels that leave the company.</summary>
public static class NotificationText
{
    public static (string Title, string Body) Describe(TicketEvent evt, TicketMapping mapping, DetailLevel detail)
    {
        var number = evt.Ticket?.Number ?? evt.TicketNumber;
        var title = Title(evt, number);

        if (evt.Type == TicketEventType.MonitorHealth)
        {
            return (title, evt.Change ?? "");
        }

        var priority = evt.Ticket is { } t ? mapping.PriorityLabels.GetValueOrDefault(t.Priority, t.Priority.ToString()) : null;

        if (detail == DetailLevel.Minimal)
        {
            return (title, string.Join(" · ", new[] { number is null ? null : $"#{number}", priority is null ? null : $"Priorità {priority}" }.Where(x => x is not null)));
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(evt.Ticket?.Title ?? evt.Title))
        {
            lines.Add(evt.Ticket?.Title ?? evt.Title!);
        }

        if (evt.Ticket is { } ticket)
        {
            var status = mapping.StatusLabels.GetValueOrDefault(ticket.Status, ticket.Status.ToString());
            lines.Add($"Priorità: {priority} · Stato: {status}");
        }

        if (Detail(evt, mapping) is { } extra)
        {
            lines.Add(extra);
        }

        return (title, string.Join("\n", lines));
    }

    private static string Title(TicketEvent evt, string? number)
    {
        var n = number is null ? "" : $" #{number}";
        return evt.Type switch
        {
            TicketEventType.TicketCreated => $"Nuovo ticket{n}",
            TicketEventType.MessageReceived => evt.Subtype switch
            {
                "Email" => $"Nuova e-mail su{n}",
                "Note" => $"Nuova nota su{n}",
                "Comment" => $"Nuovo commento su{n}",
                "Reply" => $"Nuova risposta su{n}",
                _ => $"Nuovo messaggio su{n}",
            },
            TicketEventType.CustomerReplied => $"Il cliente ha risposto su{n}",
            TicketEventType.ReplySent => $"Risposta inviata su{n}",
            TicketEventType.TicketAssigned => $"Ticket{n} assegnato",
            TicketEventType.TicketReassigned => evt.Subtype == "Unassigned" ? $"Ticket{n} senza assegnatario" : $"Ticket{n} riassegnato",
            TicketEventType.QueueChanged => $"Ticket{n}: cambio coda",
            TicketEventType.Level2AssigneeChanged => $"Ticket{n}: cambio utente di livello 2",
            TicketEventType.StatusChanged => evt.Subtype switch
            {
                "Reopened" => $"Ticket{n} riaperto",
                "Resolved" => $"Ticket{n} risolto",
                "Closed" => $"Ticket{n} chiuso",
                "Cancelled" => $"Ticket{n} annullato",
                "Merged" => $"Ticket{n} unito",
                "Disappeared" => $"Ticket{n} non più visibile",
                "LevelChanged" => $"Ticket{n} passato di livello",
                _ => $"Ticket{n}: cambio di stato",
            },
            TicketEventType.PriorityChanged => evt.Subtype == "Increased" ? $"Ticket{n}: priorità aumentata" : $"Ticket{n}: priorità ridotta",
            TicketEventType.ReminderFlagged => $"Sollecito su{n}",
            TicketEventType.Unattended => $"Ticket{n} senza risposta",
            TicketEventType.SlaWarning => $"Ticket{n}: SLA in scadenza",
            TicketEventType.SlaBreached => $"Ticket{n}: SLA scaduto",
            TicketEventType.MonitorHealth => evt.Subtype switch
            {
                "AuthExpired" => "LiveHelpDesk: accesso a Dynamics scaduto",
                "AccessDenied" => "LiveHelpDesk: accesso a Dynamics negato",
                "DynamicsUnreachable" => "LiveHelpDesk: Dynamics non raggiungibile",
                "Throttled" => "LiveHelpDesk: Dynamics limita le richieste",
                "PollingError" => "LiveHelpDesk: errore nel monitoraggio",
                "GapDetected" => "LiveHelpDesk: possibili eventi persi",
                "Recovered" => "LiveHelpDesk: monitoraggio ripristinato",
                "BaselineCompleted" => "LiveHelpDesk: monitoraggio avviato",
                _ => "LiveHelpDesk: stato del monitoraggio",
            },
            _ => $"Evento{n}",
        };
    }

    private static string? Detail(TicketEvent evt, TicketMapping mapping)
    {
        if (evt.Message is { } message)
        {
            var from = string.IsNullOrWhiteSpace(message.From) ? null : $"Da: {message.From}";
            var subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject;
            return string.Join(" — ", new[] { from, subject }.Where(x => x is not null)) is { Length: > 0 } text ? text : null;
        }

        if (evt.Type == TicketEventType.StatusChanged && evt.Change is { } change && TryParseTransition(change, out var from2, out var to))
        {
            var before = from2 < 0 ? "—" : mapping.StatusLabels.GetValueOrDefault(from2, from2.ToString());
            var after = mapping.StatusLabels.GetValueOrDefault(to, to.ToString());
            return $"{before} → {after}";
        }

        return evt.Type == TicketEventType.Unattended ? evt.Change : null;
    }

    private static bool TryParseTransition(string change, out int from, out int to)
    {
        from = to = 0;
        var parts = change.Split("->");
        return parts.Length == 2 && int.TryParse(parts[0], out from) && int.TryParse(parts[1], out to);
    }
}
