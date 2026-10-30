using System.Text;
using LiveHelpDesk.Core;
using LiveHelpDesk.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// LiveHelpDesk.Service <command> [--data <folder>]
//   run        (default) the monitor: polls Dynamics for every user who signed in, serves the desktop apps.
//              This is what the Windows service runs; started by hand it is the same thing, for trying it out.
//   login      signs in to Dynamics (device code, with your passkey); the service does the sign-in, not this program
//   status     what the monitor knows, what it has queued, the latest events
//   dry-run    tries the rules on today's tickets, as if each had just been created (read only)
//   install | uninstall | start | stop    manage the Windows service (administrator needed)
var command = ParseCommand(args);
var dataIndex = Array.IndexOf(args, "--data");
if (dataIndex >= 0 && dataIndex + 1 < args.Length)
{
    // Must happen before anything touches AppPaths.
    Environment.SetEnvironmentVariable("LIVEHELPDESK_DATA", args[dataIndex + 1]);
}

Console.OutputEncoding = Encoding.UTF8;

switch (command)
{
    case "login" or "status" or "dry-run":
        return await Cli.RunAsync(command);

    case "install":
    {
        var installed = await ServiceInstaller.RunAsync(ServiceInstaller.InstallCommands(Environment.ProcessPath!), Console.WriteLine);
        return installed != 0 ? installed : await ServiceInstaller.RunAsync([ServiceInstaller.StartCommand()], Console.WriteLine);
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
        Console.WriteLine("Comandi: run | login | status | dry-run | install | uninstall | start | stop   (opzione: --data <cartella>)");
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

await builder.Build().RunAsync();
return 0;

static string ParseCommand(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--data")
        {
            i++; // its value is not a command
        }
        else if (!args[i].StartsWith("--"))
        {
            return args[i];
        }
    }

    return "run";
}
