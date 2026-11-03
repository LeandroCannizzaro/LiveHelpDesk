using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.ViewModels;

public sealed partial class ChannelRow : ObservableObject
{
    private readonly Func<string, Task<ChannelTestResult>> _test;

    public ChannelRow(ChannelInfo info, Func<string, Task<ChannelTestResult>> test)
    {
        _test = test;
        Id = info.Id;
        Name = info.Id switch { "email" => "E-mail", "teams" => "Teams", "sms" => "SMS", "whatsapp" => "WhatsApp", _ => info.Id };
        Enabled = info.Enabled;
        Detail = !info.Enabled
            ? "spento"
            : info.State == "Open" ? $"non risponde: {info.LastError}"
            : info.LastError is not null ? $"ultimo errore: {info.LastError}"
            : info.LastSuccess is { } ok ? $"ultimo invio {ok.ToLocalTime():dd/MM HH:mm} · {info.SentLastHour} nell'ultima ora"
            : "attivo";
        if (info.Pending > 0)
        {
            Detail += $" · {info.Pending} in coda";
        }
    }

    public string Id { get; }

    public string Name { get; }

    public bool Enabled { get; }

    public string Detail { get; }

    [ObservableProperty]
    private string _testResult = "";

    [RelayCommand]
    private async Task TestAsync()
    {
        TestResult = "Invio di prova…";
        var result = await _test(Id);
        TestResult = result.Ok ? "Messaggio di prova inviato." : $"Non riuscito: {result.Error}";
    }
}

/// <summary>The service's state, the sign-in, and how each outside channel is doing.</summary>
public sealed partial class StatusViewModel(
    Func<ILiveHelpDeskApi?> api, Func<string, Task> copyToClipboard, Action<Uri> openInBrowser) : ObservableObject
{
    [ObservableProperty]
    private string _headline = "Connessione al servizio…";

    [ObservableProperty]
    private string _details = "";

    /// <summary>"ok", "warn", "bad" or "idle": picks the colour of the dot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsWarn), nameof(IsBad))]
    private string _kind = "idle";

    [ObservableProperty]
    private bool _showLogin;

    [ObservableProperty]
    private bool _loginPending;

    [ObservableProperty]
    private string _loginCode = "";

    [ObservableProperty]
    private string _loginHelp = "";

    [ObservableProperty]
    private string _error = "";

    private Uri? _loginUrl;

    /// <summary>Start, stop, install and remove the monitor; set by the host.</summary>
    public HostingViewModel? Hosting { get; set; }

    public bool IsOk => Kind == "ok";
    public bool IsWarn => Kind == "warn";

    public bool IsBad => Kind == "bad";

    public ObservableCollection<ChannelRow> Channels { get; } = [];

    /// <summary>The dashboard address the service wants the browser to start from.</summary>
    public Uri? HomeUrl { get; private set; }

    public async Task RefreshAsync(LinkState link)
    {
        if (link != LinkState.Connected || api() is not { } service)
        {
            (Headline, Kind) = link == LinkState.ServiceMissing
                ? ("Il servizio LiveHelpDesk non è in esecuzione", "bad")
                : ("Connessione al servizio…", "idle");
            Details = link == LinkState.ServiceMissing ? "L'app riprova da sola. Si può avviare qui sotto, come processo dell'app o come servizio di Windows." : "";
            ShowLogin = false;
            Channels.Clear();
            return;
        }

        try
        {
            Apply(await service.GetStatusAsync());
            var channels = await service.GetChannelStatusAsync();
            Channels.Clear();
            foreach (var channel in channels)
            {
                Channels.Add(new ChannelRow(channel, id => TestChannelAsync(service, id)));
            }
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException)
        {
            (Headline, Kind, Details) = ("Il servizio non risponde", "bad", ex.Message);
        }
    }

    public void Apply(ServiceStatus status)
    {
        HomeUrl = status.HomeUrl;
        (Headline, Kind) = status.State switch
        {
            ProfileState.NeedsLogin => ("Serve l'accesso a Dynamics", "bad"),
            ProfileState.LoggingIn => ("In attesa dell'accesso…", "warn"),
            ProfileState.Starting => ("Avvio del monitoraggio…", "warn"),
            ProfileState.Degraded => ("Problemi temporanei nel monitoraggio", "warn"),
            _ => ($"Tutto in ordine{(status.UserName is null ? "" : $" · {status.UserName}")}", "ok"),
        };

        var lines = new List<string>();
        if (status.LastPollAt is { } poll)
        {
            lines.Add($"Ultimo controllo: {poll.ToLocalTime():HH:mm:ss}");
        }

        lines.Add($"Ticket attivi: {status.ActiveTickets}");
        if (status.PendingNotifications > 0)
        {
            lines.Add($"Notifiche in coda: {status.PendingNotifications}");
        }

        Details = string.Join("  ·  ", lines);
        Error = status.LastError ?? "";
        ShowLogin = status.State is ProfileState.NeedsLogin or ProfileState.LoggingIn;
        LoginPending = status.State == ProfileState.LoggingIn;
        if (!LoginPending)
        {
            LoginCode = "";
            LoginHelp = "";
        }
    }

    [RelayCommand]
    private async Task StartLoginAsync()
    {
        if (api() is not { } service)
        {
            return;
        }

        try
        {
            var challenge = await service.StartLoginAsync();
            _loginUrl = Uri.TryCreate(challenge.VerificationUrl, UriKind.Absolute, out var url) ? url : null;
            LoginCode = challenge.UserCode;
            LoginHelp = $"Apri {challenge.VerificationUrl}, inserisci il codice e conferma con Windows Hello. Il codice scade alle {challenge.ExpiresAt.ToLocalTime():HH:mm}.";
            LoginPending = true;
            ShowLogin = true;
            if (_loginUrl is not null)
            {
                openInBrowser(_loginUrl);
            }
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or TimeoutException)
        {
            Error = $"Impossibile avviare l'accesso: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task CopyCodeAsync() => string.IsNullOrEmpty(LoginCode) ? Task.CompletedTask : copyToClipboard(LoginCode);

    [RelayCommand]
    private void OpenLoginPage()
    {
        if (_loginUrl is not null)
        {
            openInBrowser(_loginUrl);
        }
    }

    private static async Task<ChannelTestResult> TestChannelAsync(ILiveHelpDeskApi service, string id)
    {
        try
        {
            return await service.TestChannelAsync(id);
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException)
        {
            return new ChannelTestResult(false, ex.Message);
        }
    }
}
