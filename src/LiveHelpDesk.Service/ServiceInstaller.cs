using System.Diagnostics;
using System.Security.Principal;

namespace LiveHelpDesk.Service;

/// <summary>
/// Installing a Windows service needs an administrator, once. The commands are built here as plain data so
/// they can be checked without touching the machine; running them is a thin wrapper around <c>sc.exe</c>.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "LiveHelpDesk";

    /// <summary>A virtual account: no password to store or rotate, and a SID of its own for file permissions.</summary>
    public const string ServiceAccount = "NT SERVICE\\LiveHelpDesk";

    public static IReadOnlyList<string[]> InstallCommands(string exePath) =>
    [
        ["create", ServiceName, "binPath=", $"\"{exePath}\" run", "start=", "delayed-auto", "obj=", ServiceAccount, "DisplayName=", "LiveHelpDesk"],
        ["description", ServiceName, "Controlla i ticket di assistenza su Dynamics 365 e invia le notifiche."],

        // Restart after a crash: 5 s, 30 s, then every minute; the failure count resets after a day.
        ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/30000/restart/60000"],
        ["failureflag", ServiceName, "1"],
    ];

    public static IReadOnlyList<string[]> UninstallCommands() =>
    [
        ["stop", ServiceName],
        ["delete", ServiceName],
    ];

    public static string[] StartCommand() => ["start", ServiceName];

    public static string[] StopCommand() => ["stop", ServiceName];

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Runs the commands in order; stops at the first one that fails (except tolerated ones).</summary>
    public static async Task<int> RunAsync(IEnumerable<string[]> commands, Action<string> output, params string[] tolerate)
    {
        if (!IsElevated())
        {
            output("Servono privilegi di amministratore: aprire un prompt dei comandi \"come amministratore\" e ripetere.");
            return 5;
        }

        foreach (var command in commands)
        {
            var (code, text) = await RunScAsync(command);
            output($"sc {string.Join(' ', command.Take(2))}: {(code == 0 ? "ok" : text.Trim())}");
            if (code != 0 && !tolerate.Contains(command[0]))
            {
                return code;
            }
        }

        return 0;
    }

    private static async Task<(int Code, string Output)> RunScAsync(string[] arguments)
    {
        var info = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var text = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, text);
    }
}
