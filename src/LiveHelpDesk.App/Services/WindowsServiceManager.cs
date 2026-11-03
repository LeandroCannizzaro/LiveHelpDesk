using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace LiveHelpDesk.App.Services;

public enum WindowsServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,

    /// <summary>It exists, but this account cannot read its state.</summary>
    Unknown,
}

public enum ServiceAction
{
    Install,
    Uninstall,
    Start,
    Stop,
}

public sealed record ServiceActionResult(bool Ok, string Message);

/// <summary>Runs a program, asking Windows for administrator rights (UAC) when the app does not have them.</summary>
public interface IElevatedRunner
{
    bool IsElevated { get; }

    /// <summary>The exit code; <see cref="UacRunner.Declined"/> when the user refused the prompt.</summary>
    Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class UacRunner : IElevatedRunner
{
    public const int Declined = 1223;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            // runas needs the shell; the window is hidden so it does not flash. The outcome is the exit code.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        };
        if (!IsElevated)
        {
            info.Verb = "runas";
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"Impossibile avviare {executablePath}.");
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Declined)
        {
            return Declined;
        }
    }
}

public interface IWindowsServiceManager
{
    /// <summary>True when the app already has administrator rights, so Windows will not ask.</summary>
    bool IsElevated { get; }

    WindowsServiceState Query();

    /// <summary>Install, uninstall, start or stop the Windows service. Windows asks for administrator rights if needed.</summary>
    Task<ServiceActionResult> RunAsync(ServiceAction action, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Windows service, from the app. Reading its state needs nothing special; changing it does, so the service
/// program itself (<c>install</c>, <c>uninstall</c>, <c>start</c>, <c>stop</c>) is run elevated and its exit code is the answer.
/// </summary>
public sealed class WindowsServiceManager(Func<string?> findExecutable, IElevatedRunner runner, string serviceName = "LiveHelpDesk") : IWindowsServiceManager
{
    public bool IsElevated => runner.IsElevated;

    public WindowsServiceState Query()
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running or ServiceControllerStatus.ContinuePending => WindowsServiceState.Running,
                ServiceControllerStatus.StartPending => WindowsServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => WindowsServiceState.Stopping,
                _ => WindowsServiceState.Stopped,
            };
        }
        catch (InvalidOperationException ex)
        {
            return ex.InnerException is Win32Exception { NativeErrorCode: 1060 } ? WindowsServiceState.NotInstalled : WindowsServiceState.Unknown;
        }
    }

    public async Task<ServiceActionResult> RunAsync(ServiceAction action, CancellationToken cancellationToken = default)
    {
        var executable = findExecutable();
        if (executable is null)
        {
            return new ServiceActionResult(false, $"Non trovo {ServiceExecutable.FileName}: deve stare accanto all'app.");
        }

        var verb = action switch
        {
            ServiceAction.Install => "install",
            ServiceAction.Uninstall => "uninstall",
            ServiceAction.Start => "start",
            _ => "stop",
        };

        return Explain(action, await runner.RunAsync(executable, [verb], cancellationToken));
    }

    public static ServiceActionResult Explain(ServiceAction action, int code) => (action, code) switch
    {
        (ServiceAction.Install, 0) => new(true, "Servizio installato e avviato."),
        (ServiceAction.Uninstall, 0) => new(true, "Servizio rimosso."),
        (ServiceAction.Start, 0) => new(true, "Servizio avviato."),
        (ServiceAction.Stop, 0) => new(true, "Servizio fermato."),
        (_, UacRunner.Declined) => new(false, "Operazione annullata: servono i permessi di amministratore."),
        (_, 5) => new(false, "Servono i permessi di amministratore."),
        (ServiceAction.Install, 1073) => new(false, "Il servizio è già installato."),
        (ServiceAction.Uninstall, 1060) => new(true, "Il servizio non era installato."),
        (ServiceAction.Start, 1056) => new(true, "Il servizio era già in esecuzione."),
        (ServiceAction.Stop, 1062) => new(true, "Il servizio era già fermo."),
        (ServiceAction.Start or ServiceAction.Install, 1069) => new(false, "Il servizio non riesce ad accedere con il suo account (errore 1069)."),
        _ => new(false, $"Operazione non riuscita (codice {code})."),
    };
}
