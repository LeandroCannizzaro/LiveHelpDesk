using System.Security.Principal;
using System.Text;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Sources;
using LiveHelpDesk.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// LiveHelpDesk.Service [run|login|status|dry-run] [--data <folder>]
//   run      (default) polls Dynamics until stopped; also what the Windows service does
//   login    signs in to Dynamics with a device code (once; the token is then renewed silently)
//   status   shows what the monitor knows and has queued
//   dry-run  tries the rules on today's tickets, as if each had just been created
var command = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "run";
var dataIndex = Array.IndexOf(args, "--data");
if (dataIndex >= 0 && dataIndex + 1 < args.Length)
{
    // Must happen before anything touches AppPaths.
    Environment.SetEnvironmentVariable("LIVEHELPDESK_DATA", args[dataIndex + 1]);
}

Console.OutputEncoding = Encoding.UTF8;

var sid = WindowsIdentity.GetCurrent().User!.Value;
var profileDirectory = AppPaths.ProfileDirectory(sid);
var configPath = Path.Combine(AppPaths.MachineRoot, "config.json");

if (command is "login" or "status" or "dry-run")
{
    using var loggers = Cli.ConsoleLogger();
    using var pipeline = await Pipeline.CreateAsync(profileDirectory, configPath, loggers);
    try
    {
        return command switch
        {
            "login" => await Cli.LoginAsync(pipeline),
            "status" => await Cli.StatusAsync(pipeline),
            _ => await Cli.DryRunAsync(pipeline),
        };
    }
    catch (TicketSourceException ex)
    {
        Console.Error.WriteLine($"Errore: {ex.Message}");
        if (ex.Failure == SourceFailure.AuthRequired)
        {
            Console.Error.WriteLine("Eseguire: LiveHelpDesk.Service login");
        }

        return 2;
    }
}

if (command != "run")
{
    Console.WriteLine("Comandi: run | login | status | dry-run   (opzione: --data <cartella>)");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// One log pipeline: Serilog (console + rolling file), plus the Windows Event Log when running as a service.
builder.Logging.ClearProviders();
builder.Services.AddWindowsService(options => options.ServiceName = "LiveHelpDesk");
builder.Services.AddSerilog(
    logger => logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(profileDirectory, "logs", "service-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14),
    writeToProviders: true);

builder.Services.AddSingleton(services =>
    Pipeline.CreateAsync(profileDirectory, configPath, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>())
        .GetAwaiter().GetResult());
builder.Services.AddHostedService<PollingWorker>();

await builder.Build().RunAsync();
return 0;
