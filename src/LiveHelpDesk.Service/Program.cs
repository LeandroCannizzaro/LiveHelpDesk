using System.Text;
using LiveHelpDesk.Core;
using LiveHelpDesk.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;

// LiveHelpDesk.Service <command> [--data <folder>]
//   run        (default) the monitor: polls Dynamics for every user who signed in, serves the desktop apps.
//              This is what the Windows service runs; started by hand it is the same thing, for trying it out.
//   login      signs in to Dynamics (device code, with your passkey); the service does the sign-in, not this program
//   login-mail | logout-mail    signs in to / out of Outlook, the account the e-mail channel sends as (when its provider is Outlook)
//   status     what the monitor knows, what it has queued, the latest events
//   dry-run    tries the rules on today's tickets, as if each had just been created (read only)
//   channels | channels-template | channels-import <file> | test <channel>    the outside channels (e-mail, Teams, SMS, WhatsApp)
//   install | uninstall | start | stop    manage the Windows service (administrator needed)
var (command, argument) = ParseCommand(args);
var dataIndex = Array.IndexOf(args, "--data");
if (dataIndex >= 0 && dataIndex + 1 < args.Length)
{
    // Must happen before anything touches AppPaths.
    Environment.SetEnvironmentVariable("LIVEHELPDESK_DATA", args[dataIndex + 1]);
}

Console.OutputEncoding = Encoding.UTF8;

switch (command)
{
    case "login" or "login-mail" or "logout-mail" or "status" or "dry-run" or "channels" or "channels-import" or "channels-template" or "test":
        return await Cli.RunAsync(command, argument: argument);

    case "install":
    {
        var installed = await ServiceInstaller.RunAsync(ServiceInstaller.InstallCommands(Environment.ProcessPath!), Console.WriteLine);
        if (installed != 0)
        {
            return installed;
        }

        await ServiceInstaller.GrantDataAccessAsync(AppPaths.MachineRoot, Console.WriteLine);
        return await ServiceInstaller.RunAsync([ServiceInstaller.StartCommand()], Console.WriteLine);
    }

    case "uninstall":
        return await ServiceInstaller.RunAsync(ServiceInstaller.UninstallCommands(), Console.WriteLine, tolerate: "stop");

    case "start":
        return await ServiceInstaller.RunAsync([ServiceInstaller.StartCommand()], Console.WriteLine);

    case "stop":
        return await ServiceInstaller.RunAsync([ServiceInstaller.StopCommand()], Console.WriteLine);

    case "run":
        break;

    default:
        Console.WriteLine("Comandi: run | login | login-mail | logout-mail | status | dry-run | channels | channels-template | channels-import <file> | test <canale> | install | uninstall | start | stop   (opzione: --data <cartella>)");
        return 1;
}

var configPath = Path.Combine(AppPaths.MachineRoot, "config.json");
var logDirectory = Path.Combine(AppPaths.MachineRoot, "logs");

var builder = Host.CreateApplicationBuilder(args);

// One log pipeline: Serilog (console + rolling file), plus the Windows Event Log when running as a service.
builder.Logging.ClearProviders();
builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName);
builder.Services.AddSerilog(
    logger => logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(logDirectory, "service-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14),
    writeToProviders: true);

builder.Services.AddSingleton(services =>
{
    var loggers = services.GetRequiredService<ILoggerFactory>();
    return new ProfileManager(sid => ProfileFactory.CreateAsync(sid, configPath, loggers), loggers.CreateLogger<ProfileManager>());
});
builder.Services.AddHostedService<ProfilesWorker>();
builder.Services.AddHostedService<PipeHostedService>();
if (!WindowsServiceHelpers.IsWindowsService())
{
    // Started by hand or by the app: the app can ask it to stop. The real service is only stopped through the SCM.
    builder.Services.AddHostedService<StopSignalService>();
}

await builder.Build().RunAsync();
return 0;

static (string Command, string? Argument) ParseCommand(string[] args)
{
    string? command = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--data")
        {
            i++; // its value is not a command
        }
        else if (!args[i].StartsWith("--"))
        {
            if (command is null)
            {
                command = args[i];
            }
            else
            {
                return (command, args[i]);
            }
        }
    }

    return (command ?? "run", null);
}
