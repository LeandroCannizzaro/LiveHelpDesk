using System.Diagnostics;
using System.Media;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.Core;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App;

/// <summary>Wires the app together and owns its lifetime: the service link, the tray, the overlay, the window.</summary>
public sealed class AppHost
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly string[] _args;
    private readonly AttentionModel _attention = new();
    private readonly ServiceLink _link = new(IpcProtocol.PipeName);
    private readonly HttpClient _http = CreateHttp();
    private readonly InstallKind _installKind = InstallKindDetector.Detect();

    private SingleInstance? _single;
    private UiSettings _settings = new();
    private MainWindow _window = null!;
    private StatusViewModel _status = null!;
    private HostingViewModel _hosting = null!;
    private MonitorProcessHost _monitor = null!;
    private EventsViewModel _events = null!;
    private RulesViewModel _rules = null!;
    private NotificationsViewModel _notifications = null!;
    private ChannelsViewModel _channels = null!;
    private SettingsViewModel _appSettings = null!;
    private UpdatesViewModel _updates = null!;
    private NotificationPump _pump = null!;
    private ScreenShareGuard _shareGuard = null!;
    private OverlayController _overlay = null!;
    private TrayController _tray = null!;
    private ProfileState? _profile;
    private string _headline = "";

    public AppHost(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        _args = lifetime.Args ?? [];
    }

    public void Start()
    {
        // The app is the tray icon: it must keep running with no window open.
        _lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _single = SingleInstance.TryAcquire("LiveHelpDesk.App", () => Dispatcher.UIThread.Post(() => _window?.Bring()));
        if (_single is null)
        {
            // A second instance: the first one has been asked to show itself, so this one bows out.
            // Shutdown is posted, not called inline: Start() runs inside OnFrameworkInitializationCompleted,
            // before Avalonia's main loop exists, and shutting the dispatcher down here makes the pending
            // PushFrame throw "Dispatcher shut down". Posting lets the loop start and exit cleanly.
            Dispatcher.UIThread.Post(() => _lifetime.Shutdown());
            return;
        }

        _settings = UiSettings.Load();
        _shareGuard = new ScreenShareGuard(_settings, new WindowsScreenShareDetector());
        ToastService.Listen(action => Dispatcher.UIThread.Post(() => _ = HandleToastAsync(action)));

        _window = new MainWindow();
        _status = new StatusViewModel(() => _link.Api, CopyAsync, OpenInBrowser);
        _hosting = CreateHosting();
        _status.Hosting = _hosting;
        _events = new EventsViewModel(() => _link.Api, OpenRowAsync, AcknowledgeAsync);
        _rules = new RulesViewModel(() => _link.Api);
        _notifications = new NotificationsViewModel(() => _link.Api);
        _channels = new ChannelsViewModel(() => _link.Api, CopyAsync, OpenInBrowser);
        _appSettings = new SettingsViewModel(
            _settings, new AutoStart(), Environment.ProcessPath ?? "LiveHelpDesk.exe", _window.ScreenNames(),
            ShowOverlayExample, () => _overlay?.Reposition());
        _updates = CreateUpdates();
        _appSettings.Updates = _updates;
        _appSettings.Service = () => _link.Api;
        _window.Attach(_events, _status, _rules, _notifications, _channels, _appSettings, _shareGuard);
        _window.SetSidebarCollapsed(_settings.SidebarCollapsed);
        _window.SidebarCollapsedChanged += collapsed =>
        {
            _settings.SidebarCollapsed = collapsed;
            _settings.Save();
        };
        _window.PaneChanged += pane => _ = OnPaneChangedAsync(pane);

        // One policy shared by the overlay and the toast pump: hold uncontrolled, on-screen notifications back
        // during a presentation / screen share, or while a VPN is up — each governed by its own user switch.
        var suppression = new SuppressionPolicy(_settings, new WindowsDoNotDisturb(), new WindowsVpnDetector(), _shareGuard);
        _pump = new NotificationPump(_link, _attention, new ToastService(), () => DateTimeOffset.UtcNow, suppression);
        var overlayModel = new OverlayViewModel(OpenHeadlineAsync, AcknowledgeAllAsync, duration =>
        {
            _attention.Snooze(duration, DateTimeOffset.UtcNow);
        });
        _overlay = new OverlayController(
            _attention, _settings, suppression, () => DateTimeOffset.UtcNow, overlayModel,
            () => SystemSounds.Exclamation.Play());

        _tray = new TrayController(
            () => _window.Bring(),
            _window.ToggleAlerts,
            () => OpenInBrowser(_window.Home),
            _window.ShowAbout,
            Exit);
        TrayIcon.SetIcons(Application.Current!, [_tray.Icon]);
        _window.AlertsStateChanged += state => _tray.SetAlertsActive(state == Views.AlertsState.Active);
        _window.RefreshAlertsButton();

        _attention.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshChrome);
        _link.StateChanged += state => Dispatcher.UIThread.Post(() => _ = OnLinkStateAsync(state));
        _link.Changed += (_, e) => Dispatcher.UIThread.Post(() => _ = OnServiceChangedAsync(e.Kind));

        _overlay.Start();

        // A share starting or ending hides or brings back the pill at once, and the button says why alerts are paused.
        _shareGuard.Changed += () => Dispatcher.UIThread.Post(() => _overlay.Refresh());
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _shareGuard.Update()).Start();
        RefreshChrome();
        _link.Start();

        // A safety net: even if a change notice is missed, the app catches up within seconds.
        new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => _ = CatchUpAsync()).Start();

        // Updates: a first look shortly after start (not to slow it down), then every few hours.
        DispatcherTimer.RunOnce(CheckForUpdates, TimeSpan.FromSeconds(30));
        new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, (_, _) => CheckForUpdates()).Start();

        if (!_args.Contains("--minimized"))
        {
            _window.Show();
        }

        if (_args.Contains("--demo"))
        {
            RunDemo();
        }

        if (_args.Contains("--pane-test"))
        {
            RunPaneTest();
        }

        if (ExitAfter() is { } seconds)
        {
            DispatcherTimer.RunOnce(Exit, TimeSpan.FromSeconds(seconds));
        }
    }

    /// <summary>Shows a pane's current data when the user opens it, but never over edits that are not saved.</summary>
    private async Task OnPaneChangedAsync(Pane pane)
    {
        if (pane == Pane.Settings)
        {
            await _appSettings.LoadPollAsync();
        }

        if (_link.State != LinkState.Connected)
        {
            return;
        }

        switch (pane)
        {
            case Pane.Rules when !_rules.IsDirty:
                await _rules.LoadAsync();
                break;
            case Pane.Notifications when !_notifications.IsDirty:
                await _notifications.LoadAsync();
                break;
            case Pane.Channels when !_channels.IsDirty:
                await _channels.LoadAsync();
                break;
            case Pane.Events:
                await _events.RefreshAsync();
                break;
            case Pane.Status:
                await _status.RefreshAsync(_link.State);
                break;
        }
    }

    /// <summary>Settings → "Mostra un esempio": the overlay with a sample ticket, so the screen and position can be judged.</summary>
    private void ShowOverlayExample()
    {
        _attention.Add(
            new AttentionItem(
                $"example:{DateTimeOffset.UtcNow.Ticks}", "Esempio: nuovo ticket #261000000",
                "Cliente: Mario Rossi\nOggetto: Richiesta di supporto\nPriorità: Normale\nPremi la freccia per ridurre la finestrella a una sola riga.",
                Severity.Attention, null, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
        _attention.EndSnooze();
    }

    private async Task OnLinkStateAsync(LinkState state)
    {
        await _status.RefreshAsync(state);
        _hosting.Refresh(state);
        await _hosting.AutoStartAsync(state);
        if (_window.CurrentPane == Pane.Settings)
        {
            await _appSettings.LoadPollAsync();
        }
        if (state == LinkState.Connected)
        {
            await CatchUpAsync();
        }
        else
        {
            _profile = null;
        }

        RefreshChrome();
    }

    private async Task OnServiceChangedAsync(ChangeKind kind)
    {
        if (kind == ChangeKind.Notifications)
        {
            await _pump.PumpAsync();
        }

        await RefreshServiceStateAsync();
        if (kind == ChangeKind.Events)
        {
            await _events.RefreshAsync();
        }
    }

    private async Task CatchUpAsync()
    {
        if (_link.State != LinkState.Connected)
        {
            return;
        }

        try
        {
            await _pump.PumpAsync();
            await RefreshServiceStateAsync();
            await _events.RefreshAsync();
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException)
        {
            // The link will notice and reconnect; the next round catches up.
        }
    }

    private async Task RefreshServiceStateAsync()
    {
        await _status.RefreshAsync(_link.State);
        if (_link.Api is { } api)
        {
            try
            {
                var status = await api.GetStatusAsync();
                _profile = status.State;
                if (status.HomeUrl is { } home)
                {
                    _window.SetHome(home);
                }
            }
            catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException)
            {
                _profile = null;
            }
        }

        RefreshChrome();
    }

    /// <summary>Everything that shows state: the tray icon and tooltip, the window's side bar.</summary>
    private void RefreshChrome()
    {
        var look = TrayLooks.For(_link.State, _profile is ProfileState.NeedsLogin, _attention.Level);
        _headline = _link.State switch
        {
            LinkState.ServiceMissing => "servizio non in esecuzione",
            LinkState.Connecting => "connessione al servizio…",
            _ => _profile switch
            {
                ProfileState.NeedsLogin => "serve l'accesso a Dynamics",
                ProfileState.LoggingIn => "in attesa dell'accesso",
                ProfileState.Degraded => "problemi temporanei",
                _ => "in funzione",
            },
        };

        var waiting = _attention.Count;
        _tray.Update(look, waiting > 0 ? $"LiveHelpDesk · {waiting} da gestire" : $"LiveHelpDesk · {_headline}");
        _window.SetLinkText(_headline, waiting);
    }

    // ---- what the user can do ------------------------------------------------------------------------------

    private async Task HandleToastAsync(ToastAction action)
    {
        if (action.Action == ToastService.OpenAction)
        {
            if (action.Url is { } url)
            {
                _window.OpenTicket(url);
            }
            else
            {
                _window.Bring();
            }
        }

        await AcknowledgeAsync(action.EventKey);
    }

    private Task OpenHeadlineAsync()
    {
        var item = _attention.Headline;
        if (item?.EventKey.StartsWith(UpdateNoticePrefix, StringComparison.Ordinal) == true)
        {
            _window.ShowPane(Pane.Settings);
            _window.Bring();
        }
        else if (item?.Url is { } url)
        {
            _window.OpenTicket(url);
        }
        else
        {
            _window.ShowPane(Pane.Events);
            _window.Bring();
        }

        return item is null ? Task.CompletedTask : AcknowledgeAsync(item.EventKey);
    }

    private Task OpenRowAsync(EventRow row)
    {
        if (row.Url is { } url)
        {
            _window.OpenTicket(url);
        }

        return AcknowledgeAsync(row.Key);
    }

    private async Task AcknowledgeAsync(string eventKey)
    {
        _attention.Acknowledge(eventKey);
        if (eventKey.StartsWith(UpdateNoticePrefix, StringComparison.Ordinal))
        {
            return; // the app's own notice: the service has never heard of it
        }

        await TellServiceAsync(api => api.AcknowledgeAsync(eventKey));
    }

    private async Task AcknowledgeAllAsync()
    {
        foreach (var item in _attention.AcknowledgeAll().Where(i => !i.EventKey.StartsWith(UpdateNoticePrefix, StringComparison.Ordinal)))
        {
            await TellServiceAsync(api => api.AcknowledgeAsync(item.EventKey));
        }
    }

    private async Task TellServiceAsync(Func<ILiveHelpDeskApi, Task> call)
    {
        if (_link.Api is not { } api)
        {
            return; // offline: the local state is already updated, the service will escalate if nobody answers
        }

        try
        {
            await call(api);
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException)
        {
        }
    }

    private async Task CopyAsync(string text)
    {
        if (_window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private HostingViewModel CreateHosting()
    {
        var executable = () => ServiceExecutable.Find(AppContext.BaseDirectory);
        var services = new WindowsServiceManager(executable, new UacRunner());
        _monitor = new MonitorProcessHost(
            () => MonitorProcess.Start(executable() ?? throw new FileNotFoundException($"Non trovo {ServiceExecutable.FileName}.")),
            () => StopSignal.Raise(StopSignal.DefaultName));
        return new HostingViewModel(
            services, _monitor, _settings.StartMonitorProcess,
            value =>
            {
                _settings.StartMonitorProcess = value;
                _settings.Save();
            },
            action => Dispatcher.UIThread.Post(action));
    }

    private static void OpenInBrowser(Uri url) =>
        Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });

    // ---- updates ------------------------------------------------------------------------------------------------

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"LiveHelpDesk/{UpdateVersions.Current().ToString(3)}");
        return http;
    }

    private UpdatesViewModel CreateUpdates()
    {
        var current = UpdateVersions.Current();
        var checker = new UpdateChecker(_http, UpdateChecker.ManifestUrl(), current, _installKind);
        var updates = new UpdatesViewModel(
            current, _installKind, checker.CheckAsync, InstallUpdateAsync, OpenInBrowser, UpdateChecker.ReleaseNotesUrl(UpdateChecker.ManifestUrl()));
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdatesViewModel.IsAvailable) or nameof(UpdatesViewModel.AvailableVersion))
            {
                _window.SetUpdateAvailable(updates.IsAvailable ? updates.AvailableVersion : null);
                AnnounceUpdate(updates);
            }
        };
        return updates;
    }

    /// <summary>A new version gets one notice in the on-screen overlay; clicking it opens Settings → Aggiornamenti.</summary>
    private void AnnounceUpdate(UpdatesViewModel updates)
    {
        if (!updates.IsAvailable || updates.AvailableVersion.Length == 0 || _settings.AnnouncedUpdate == updates.AvailableVersion)
        {
            return;
        }

        _settings.AnnouncedUpdate = updates.AvailableVersion;
        _settings.Save();
        var now = DateTimeOffset.UtcNow;
        _attention.Add(
            new AttentionItem(
                UpdateNoticePrefix + updates.AvailableVersion, $"Aggiornamento disponibile: LiveHelpDesk {updates.AvailableVersion}",
                updates.CanInstall
                    ? "Premi «Apri» e poi «Aggiorna e riavvia»: le impostazioni restano come sono."
                    : "Premi «Apri» per i dettagli.",
                Severity.Attention, null, now),
            now);
    }

    private const string UpdateNoticePrefix = "update:";

    private void CheckForUpdates()
    {
        if (_updates.CheckCommand.CanExecute(null))
        {
            _ = _updates.CheckCommand.ExecuteAsync(null);
        }
    }

    /// <summary>"Aggiorna e riavvia": download, verify, hand over to the installer script, and get out of its way.</summary>
    private async Task InstallUpdateAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var package = await UpdateInstaller.DownloadAsync(
            _http, update.Package!, UpdateInstaller.DownloadDirectory(update.Version), progress, cancellationToken);
        UpdateInstaller.Launch(
            _installKind, package, AppContext.BaseDirectory, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LiveHelpDesk.exe"),
            Environment.ProcessId, Path.Combine(AppPaths.UserLogs, "update.log"));
        Exit();
    }

    private void Exit() => _ = ExitAsync();

    private async Task ExitAsync()
    {
        // A monitor this app started goes away with it; the Windows service is not touched.
        await _monitor.StopAsync();
        _window.ReallyClose = true;
        _overlay.Stop();
        _tray.Dispose();
        TrayIcon.SetIcons(Application.Current!, []);
        _single?.Dispose();
        _lifetime.Shutdown();
    }

    // ---- switches for trying it out ----------------------------------------------------------------------------

    /// <summary>--demo: shows a toast and the overlay with sample tickets, without needing the service or any real ticket.</summary>
    private void RunDemo()
    {
        var url = new Uri("https://qgs.crm4.dynamics.com/main.aspx?appid=d2ed1394-6c4e-ed11-bba3-00224899337b&pagetype=entityrecord&etn=incident&id=00000000-0000-0000-0000-000000000000");
        DispatcherTimer.RunOnce(() =>
        {
            _pump.Deliver(new NotificationInfo(0, "demo:1", "toast", "Nuovo ticket #261019389", "data fattura\nPriorità: Normale · Stato: L1 Assegnazione", Severity.Attention, url));
            _pump.Deliver(new NotificationInfo(0, "demo:1", "overlay", "Nuovo ticket #261019389", "data fattura", Severity.Attention, url));
        }, TimeSpan.FromSeconds(2));
        DispatcherTimer.RunOnce(() =>
        {
            _pump.Deliver(new NotificationInfo(0, "demo:2", "toast", "Ticket #261019267 senza risposta", "Richiesta Supporto BFA\nPriorità: Alta", Severity.Critical, url));
            _pump.Deliver(new NotificationInfo(0, "demo:2", "overlay", "Ticket #261019267 senza risposta", "Richiesta Supporto BFA", Severity.Critical, url));
        }, TimeSpan.FromSeconds(8));
    }

    /// <summary>--pane-test: switches panes by itself; the browser log shows whether the page survived being hidden.</summary>
    private void RunPaneTest()
    {
        DispatcherTimer.RunOnce(() => _window.ShowPane(Pane.Events), TimeSpan.FromSeconds(12));
        DispatcherTimer.RunOnce(() => _window.ShowPane(Pane.Status), TimeSpan.FromSeconds(14));
        DispatcherTimer.RunOnce(() => _window.ShowPane(Pane.Browser), TimeSpan.FromSeconds(16));
    }

    private int? ExitAfter()
    {
        var i = Array.IndexOf(_args, "--exit-after");
        return i >= 0 && i + 1 < _args.Length && int.TryParse(_args[i + 1], out var seconds) ? seconds : null;
    }
}
