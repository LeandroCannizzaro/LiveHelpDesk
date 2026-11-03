using System.Text.Json;
using System.Text.Json.Serialization;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.Service;

/// <summary>
/// Command line front end. It talks to the running service exactly as the desktop app does, so what works
/// here is what the app will get.
/// </summary>
public static class Cli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(
        string command, Func<string, TimeSpan, Task<IpcConnection?>>? connect = null, string? argument = null)
    {
        if (command == "channels-template")
        {
            Console.WriteLine(JsonSerializer.Serialize(Template(), Json));
            return 0;
        }

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
                "login-mail" => await LoginMailAsync(connection.Api),
                "logout-mail" => await LogoutMailAsync(connection.Api),
                "status" => await StatusAsync(connection.Api),
                "channels" => await ChannelsAsync(connection.Api),
                "channels-import" => await ImportAsync(connection.Api, argument),
                "test" => await TestAsync(connection.Api, argument),
                _ => await DryRunAsync(connection.Api),
            };
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException)
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

    private static async Task<int> LoginMailAsync(ILiveHelpDeskApi api)
    {
        var current = await api.GetMailAccountAsync();
        if (current.Account is not null)
        {
            Console.WriteLine($"Outlook è già collegato come {current.Account}. Per cambiare account: logout-mail.");
            return 0;
        }

        var challenge = await api.StartMailLoginAsync();
        Console.WriteLine();
        Console.WriteLine(challenge.Message);
        Console.WriteLine();
        Console.WriteLine("In attesa dell'accesso a Outlook (il codice scade alle " + challenge.ExpiresAt.ToLocalTime().ToString("t") + ")...");

        while (DateTimeOffset.UtcNow < challenge.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            current = await api.GetMailAccountAsync();
            if (!current.LoginPending)
            {
                break;
            }
        }

        if (current.Account is null)
        {
            Console.Error.WriteLine($"Accesso a Outlook non completato. {current.LastError}");
            return 4;
        }

        Console.WriteLine($"Accesso riuscito: le e-mail partiranno da {current.Account}.");
        return 0;
    }

    private static async Task<int> LogoutMailAsync(ILiveHelpDeskApi api)
    {
        await api.SignOutMailAsync();
        Console.WriteLine("Accesso a Outlook rimosso.");
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

    private static async Task<int> ChannelsAsync(ILiveHelpDeskApi api)
    {
        Console.WriteLine($"{"Canale",-10} {"Stato",-12} {"Circuito",-9} {"Inviati/ora",-12} {"In coda",-8} Ultimo errore");
        foreach (var channel in await api.GetChannelStatusAsync())
        {
            Console.WriteLine(
                $"{channel.Id,-10} {(channel.Enabled ? "attivo" : "spento"),-12} {channel.State,-9} {channel.SentLastHour,-12} {channel.Pending,-8} {channel.LastError}");
        }

        Console.WriteLine();
        Console.WriteLine("Impostazioni (i segreti sono nascosti):");
        Console.WriteLine(JsonSerializer.Serialize(await api.GetChannelSettingsAsync(), Json));
        return 0;
    }

    private static async Task<int> ImportAsync(ILiveHelpDeskApi api, string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Console.Error.WriteLine("Indicare il file: LiveHelpDesk.Service channels-import <file.json>   (modello: channels-template)");
            return 1;
        }

        var settings = JsonSerializer.Deserialize<ChannelSettings>(await File.ReadAllTextAsync(file), Json);
        await api.SaveChannelSettingsAsync(settings!);
        Console.WriteLine("Impostazioni salvate; i segreti sono ora cifrati dal servizio.");
        Console.WriteLine($"ATTENZIONE: il file \"{file}\" contiene ancora i segreti in chiaro. Cancellarlo ora.");
        return 0;
    }

    private static async Task<int> TestAsync(ILiveHelpDeskApi api, string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            Console.Error.WriteLine("Indicare il canale: LiveHelpDesk.Service test email|teams|sms|whatsapp");
            return 1;
        }

        Console.WriteLine($"Invio di prova su {channel}...");
        var result = await api.TestChannelAsync(channel);
        Console.WriteLine(result.Ok ? "Messaggio di prova inviato." : $"Non riuscito: {result.Error}");
        return result.Ok ? 0 : 6;
    }

    /// <summary>A starting point for <c>channels-import</c>: everything off, with the fields to fill in.</summary>
    private static ChannelSettings Template() => new()
    {
        Email = new EmailSettings { Host = "smtp.example.com", From = "helpdesk@example.com", To = ["me@example.com"], User = "helpdesk@example.com", Password = "..." },
        Teams = new WebhookSettings { Url = "https://...webhook.office.com/... (indirizzo del flusso Workflows)" },
        Sms = new TwilioSettings { AccountSid = "AC...", AuthToken = "...", From = "+39...", To = ["+39..."] },
        WhatsApp = new TwilioSettings { AccountSid = "AC...", AuthToken = "...", From = "+14155238886", To = ["+39..."], ContentSid = "HX... (modello approvato)" },
    };

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
