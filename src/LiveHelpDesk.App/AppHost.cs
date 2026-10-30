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
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App;

/// <summary>Wires the app together and owns its lifetime: the service link, the tray, the overlay, the window.</summary>
public sealed class AppHost
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly string[] _args;
    private readonly AttentionModel _attention = new();
    private readonly ServiceLink _link = new(IpcProtocol.PipeName);

    private SingleInstance? _single;
    private UiSettings _settings = new();
    private MainWindow _window = null!;
    private StatusViewModel _status = null!;
    private EventsViewModel _events = null!;
    private NotificationPump _pump = null!;
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
            _lifetime.Shutdown();
            return;
        }

        _settings = UiSettings.Load();
        ToastService.Listen(action => Dispatcher.UIThread.Post(() => _ = HandleToastAsync(action)));

        _window = new MainWindow();
        _status = new StatusViewModel(() => _link.Api, CopyAsync, OpenInBrowser);
        _events = new EventsViewModel(() => _link.Api, OpenRowAsync, AcknowledgeAsync);
        _window.Attach(_events, _status);

        _pump = new NotificationPump(_link, _attention, new ToastService(), () => DateTimeOffset.UtcNow);
        var overlayModel = new OverlayViewModel(OpenHeadlineAsync, AcknowledgeAllAsync, duration =>
        {
            _attention.Snooze(duration, DateTimeOffset.UtcNow);
        });
        _overlay = new OverlayController(
            _attention, _settings, new WindowsDoNotDisturb(), () => DateTimeOffset.UtcNow, overlayModel,
            () => SystemSounds.Exclamation.Play());

        _tray = new TrayController(
            () => _window.Bring(),
            () => _ = AcknowledgeAllAsync(),
            Exit);
        TrayIcon.SetIcons(Application.Current!, [_tray.Icon]);

        _attention.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshChrome);
        _link.StateChanged += state => Dispatcher.UIThread.Post(() => _ = OnLinkStateAsync(state));
        _link.Changed += (_, e) => Dispatcher.UIThread.Post(() => _ = OnServiceChangedAsync(e.Kind));

        _overlay.Start();
        RefreshChrome();
        _link.Start();

        // A safety net: even if a change notice is missed, the app catches up within seconds.
        new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => _ = CatchUpAsync()).Start();

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

    private async Task OnLinkStateAsync(LinkState state)
    {
        await _status.RefreshAsync(state);
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
        if (item?.Url is { } url)
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
        await TellServiceAsync(api => api.AcknowledgeAsync(eventKey));
    }

    private async Task AcknowledgeAllAsync()
    {
        foreach (var item in _attention.AcknowledgeAll())
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

    private static void OpenInBrowser(Uri url) =>
        Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });

    private void Exit()
    {
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
