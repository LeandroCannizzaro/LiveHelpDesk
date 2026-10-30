using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.Service;

/// <summary>
/// Command line front end. It talks to the running service exactly as the desktop app does, so what works
/// here is what the app will get.
/// </summary>
public static class Cli
{
    public static async Task<int> RunAsync(string command, Func<string, TimeSpan, Task<IpcConnection?>>? connect = null)
    {
        connect ??= (pipe, timeout) => IpcClient.TryConnectAsync(pipe, timeout, CancellationToken.None);
        await using var connection = await connect(IpcProtocol.PipeName, TimeSpan.FromSeconds(2));
        if (connection is null)
        {
            Console.Error.WriteLine("Il servizio LiveHelpDesk non risponde.");
            Console.Error.WriteLine("Avviarlo (servizio installato: \"LiveHelpDesk.Service start\") oppure, per provare, in un'altra finestra: \"LiveHelpDesk.Service run\".");
            return 3;
        }

        try
        {
            return command switch
            {
                "login" => await LoginAsync(connection.Api),
                "status" => await StatusAsync(connection.Api),
                _ => await DryRunAsync(connection.Api),
            };
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException)
        {
            Console.Error.WriteLine($"Errore: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> LoginAsync(ILiveHelpDeskApi api)
    {
        var status = await api.GetStatusAsync();
        if (status.State is ProfileState.Running or ProfileState.Starting or ProfileState.Degraded)
        {
            Console.WriteLine($"Già collegato come {status.UserName}. Per cambiare account serve rimuovere l'accesso dal profilo.");
            return 0;
        }

        var challenge = await api.StartLoginAsync();
        Console.WriteLine();
        Console.WriteLine(challenge.Message);
        Console.WriteLine();
        Console.WriteLine("In attesa dell'accesso (il codice scade alle " + challenge.ExpiresAt.ToLocalTime().ToString("t") + ")...");

        while (DateTimeOffset.UtcNow < challenge.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            status = await api.GetStatusAsync();
            if (status.State != ProfileState.LoggingIn)
            {
                break;
            }
        }

        if (status.State == ProfileState.LoggingIn || status.State == ProfileState.NeedsLogin)
        {
            Console.Error.WriteLine($"Accesso non completato. {status.LastError}");
            return 4;
        }

        Console.WriteLine($"Accesso riuscito come {status.UserName}.");
        return 0;
    }

    private static async Task<int> StatusAsync(ILiveHelpDeskApi api)
    {
        var status = await api.GetStatusAsync();
        Console.WriteLine($"Servizio:       versione {status.ServiceVersion}, protocollo {status.ProtocolVersion}");
        Console.WriteLine($"Stato:          {Describe(status.State)}{(status.UserName is null ? "" : $" ({status.UserName})")}");
        Console.WriteLine($"Baseline:       {status.BaselineAt?.ToLocalTime().ToString("g") ?? "non ancora eseguita"}");
        Console.WriteLine($"Ultimo giro:    {status.LastPollAt?.ToLocalTime().ToString("g") ?? "-"}");
        Console.WriteLine($"Ticket attivi:  {status.ActiveTickets}");
        Console.WriteLine($"In coda:        {status.PendingNotifications} notifiche");
        if (status.LastError is not null)
        {
            Console.WriteLine($"Ultimo errore:  {status.LastError}");
        }

        Console.WriteLine();
        Console.WriteLine("Ultimi eventi:");
        foreach (var evt in await api.GetRecentEventsAsync(15))
        {
            Console.WriteLine($"  {evt.OccurredAt.ToLocalTime():g}  {evt.Type,-22} {evt.Subtype,-14} {evt.TicketNumber}  {Trim(evt.Title)}");
        }

        return 0;
    }

    private static async Task<int> DryRunAsync(ILiveHelpDeskApi api)
    {
        var result = await api.DryRunRulesAsync();
        Console.WriteLine($"{result.TicketsEvaluated} ticket attivi valutati come \"nuovo ticket\".");
        foreach (var rule in result.Rules)
        {
            Console.WriteLine($"  {rule.RuleId,-20} {rule.Matches,4} corrispondenze  ({rule.RuleName})");
        }

        foreach (var example in result.Examples.Take(10))
        {
            Console.WriteLine($"    #{example.TicketNumber}  {Trim(example.Title)}  → {example.RuleId}");
        }

        return 0;
    }

    private static string Describe(ProfileState state) => state switch
    {
        ProfileState.NeedsLogin => "serve il login",
        ProfileState.LoggingIn => "login in corso",
        ProfileState.Starting => "avvio (baseline)",
        ProfileState.Running => "in funzione",
        _ => "in difficoltà (ultimi controlli falliti)",
    };

    private static string Trim(string? text) => text is { Length: > 50 } ? text[..50] + "…" : text ?? "";
}
