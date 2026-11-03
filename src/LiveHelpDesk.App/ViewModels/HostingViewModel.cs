using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>
/// How the monitor is hosted. Two ways, kept apart on purpose:
/// <list type="bullet">
/// <item>the Windows service (installed once by an administrator, starts with the PC): Install/Uninstall exist only
/// here, and when it is installed Start/Stop go to Windows;</item>
/// <item>a plain process the app launches itself (<c>LiveHelpDesk.Service run</c>, no administrator needed) when
/// there is no service: here Start/Stop create and end that process.</item>
/// </list>
/// </summary>
public sealed partial class HostingViewModel : ObservableObject
{
    private readonly IWindowsServiceManager _services;
    private readonly MonitorProcessHost _process;
    private readonly Action<bool> _autoStartChanged;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Action<Action> _post;
    private readonly int _verifyAttempts;
    private LinkState _link = LinkState.Connecting;
    private bool _stoppedByUser;
    private bool _autoStartTried;

    public HostingViewModel(
        IWindowsServiceManager services,
        MonitorProcessHost process,
        bool autoStart,
        Action<bool> autoStartChanged,
        Action<Action>? post = null,
        Func<TimeSpan, Task>? delay = null,
        int verifyAttempts = 20)
    {
        _services = services;
        _process = process;
        _autoStart = autoStart;
        _autoStartChanged = autoStartChanged;
        _post = post ?? (action => action());
        _delay = delay ?? (time => Task.Delay(time));
        _verifyAttempts = verifyAttempts;
        _process.Changed += () => _post(Update);
        Update();
    }

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private string _serviceLine = "";

    [ObservableProperty]
    private string _processLine = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _canStart;

    [ObservableProperty]
    private bool _canStop;

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private bool _canUninstall;

    [ObservableProperty]
    private string _startLabel = "Avvia";

    [ObservableProperty]
    private string _stopLabel = "Ferma";

    public WindowsServiceState ServiceState { get; private set; } = WindowsServiceState.Unknown;

    /// <summary>True when the Windows service exists, running or not.</summary>
    public bool ServiceInstalled => ServiceState is not (WindowsServiceState.NotInstalled or WindowsServiceState.Unknown);

    public bool NeedsAdminPrompt => !_services.IsElevated;

    partial void OnAutoStartChanged(bool value) => _autoStartChanged(value);

    /// <summary>Re-reads the Windows service's state and redraws; call it when the link to the monitor changes.</summary>
    public void Refresh(LinkState link)
    {
        _link = link;
        Update();
    }

    /// <summary>Called when the link reports the monitor missing: starts the process once, if there is no service and the user did not stop it.</summary>
    public async Task AutoStartAsync(LinkState link)
    {
        _link = link;
        if (!AutoStart || _autoStartTried || _stoppedByUser || link != LinkState.ServiceMissing)
        {
            return;
        }

        Update();
        if (ServiceState != WindowsServiceState.NotInstalled || _process.IsActive)
        {
            return;
        }

        _autoStartTried = true;
        await StartProcessAsync();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        _stoppedByUser = false;
        if (ServiceInstalled)
        {
            await RunServiceActionAsync(ServiceAction.Start);
        }
        else
        {
            await StartProcessAsync();
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _stoppedByUser = true;
        if (ServiceInstalled)
        {
            await RunServiceActionAsync(ServiceAction.Stop);
            return;
        }

        Busy = true;
        Message = "";
        try
        {
            if (_process.IsActive)
            {
                await _process.StopAsync();
                Message = "Processo fermato.";
            }
            else
            {
                // Started by an earlier copy of the app: it still listens for the same signal.
                Message = _process.SignalStop() ? "Richiesta di arresto inviata." : "Il processo non risponde.";
            }
        }
        finally
        {
            Busy = false;
            Update();
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        // Only one monitor can hold the pipe: the process this app started makes room for the service.
        if (_process.IsActive)
        {
            await _process.StopAsync();
        }

        await RunServiceActionAsync(ServiceAction.Install);
    }

    [RelayCommand]
    private Task UninstallAsync() => RunServiceActionAsync(ServiceAction.Uninstall);

    private async Task StartProcessAsync()
    {
        if (_link == LinkState.Connected)
        {
            Message = "Un monitor è già in funzione.";
            return;
        }

        Busy = true;
        Message = "";
        try
        {
            await _process.StartAsync();
            if (_process.State == MonitorProcessState.Failed)
            {
                Message = $"Non riesco ad avviare il processo: {_process.LastError}";
            }
        }
        finally
        {
            Busy = false;
            Update();
        }
    }

    private async Task RunServiceActionAsync(ServiceAction action)
    {
        Busy = true;
        Message = _services.IsElevated ? "" : "Windows chiederà i permessi di amministratore…";
        try
        {
            var result = await _services.RunAsync(action);
            if (result.Ok)
            {
                // The exit code is not trusted on its own: look at what the service is now.
                result = await VerifyAsync(action, result);
            }

            Message = result.Message;
        }
        finally
        {
            Busy = false;
            Update();
        }
    }

    private async Task<ServiceActionResult> VerifyAsync(ServiceAction action, ServiceActionResult result)
    {
        for (var attempt = 0; attempt < _verifyAttempts; attempt++)
        {
            var state = await Task.Run(_services.Query);
            ServiceState = state;
            var done = action switch
            {
                ServiceAction.Install => state is not WindowsServiceState.NotInstalled,
                ServiceAction.Uninstall => state is WindowsServiceState.NotInstalled,
                ServiceAction.Start => state is WindowsServiceState.Running,
                _ => state is WindowsServiceState.Stopped,
            };
            if (done)
            {
                return result;
            }

            await _delay(TimeSpan.FromMilliseconds(500));
        }

        return new ServiceActionResult(false, "L'operazione sembrava riuscita ma lo stato del servizio non è cambiato.");
    }

    private void Update()
    {
        ServiceState = _services.Query();
        ServiceLine = ServiceState switch
        {
            WindowsServiceState.NotInstalled => "Servizio Windows: non installato",
            WindowsServiceState.Stopped => "Servizio Windows: installato, fermo",
            WindowsServiceState.Starting => "Servizio Windows: in avvio…",
            WindowsServiceState.Running => "Servizio Windows: installato, in esecuzione",
            WindowsServiceState.Stopping => "Servizio Windows: in arresto…",
            _ => "Servizio Windows: stato non leggibile",
        };

        ProcessLine = _process.State switch
        {
            MonitorProcessState.Running => $"Processo avviato dall'app: in esecuzione (PID {_process.ProcessId})",
            MonitorProcessState.Restarting => "Processo avviato dall'app: si è fermato, lo riavvio…",
            MonitorProcessState.Failed => $"Processo avviato dall'app: non riesce a partire ({_process.LastError})",
            _ => "Processo avviato dall'app: nessuno",
        };

        var installed = ServiceInstalled;
        StartLabel = installed ? "Avvia il servizio" : "Avvia il processo";
        StopLabel = installed ? "Ferma il servizio" : "Ferma il processo";
        CanStart = !Busy && (installed
            ? ServiceState == WindowsServiceState.Stopped
            : !_process.IsActive && _link != LinkState.Connected);
        CanStop = !Busy && (installed
            ? ServiceState is WindowsServiceState.Running or WindowsServiceState.Starting
            : _process.IsActive || _link == LinkState.Connected);
        CanInstall = !Busy && ServiceState == WindowsServiceState.NotInstalled;
        CanUninstall = !Busy && installed;
    }

    partial void OnBusyChanged(bool value) => Update();
}
