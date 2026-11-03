using System.Security.Principal;
using LiveHelpDesk.Core;
using LiveHelpDesk.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

public static class ProfileFactory
{
    public static async Task<IProfile> CreateAsync(string sid, string configPath, ILoggerFactory loggers)
    {
        // The SID comes from a pipe client's identity, but it ends up in a path: accept nothing but a real SID.
        _ = new SecurityIdentifier(sid);

        var directory = AppPaths.ProfileDirectory(sid);
        ProfileSecurity.EnsureProtected(Path.GetDirectoryName(directory)!);
        ProfileSecurity.EnsureProtected(directory);

        var pipeline = await Pipeline.CreateAsync(directory, configPath, loggers);
        return new ProfileHost(pipeline, loggers.CreateLogger($"Profile.{sid}"));
    }
}

/// <summary>At startup, resumes the monitors of everyone who has signed in before.</summary>
public sealed class ProfilesWorker(ProfileManager profiles, ILogger<ProfilesWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(AppPaths.MachineRoot, "profiles");
        if (!Directory.Exists(root))
        {
            return;
        }

        var returning = Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "msal-cache.bin")))
            .Select(d => Path.GetFileName(d)!)
            .ToList();

        logger.LogInformation("Resuming {Count} profile(s)", returning.Count);
        await profiles.StartExistingAsync(returning);
    }
}

/// <summary>
/// Ends the monitor when the app that started it (<c>run</c>) raises the stop signal, so it shuts down cleanly
/// instead of being killed. Not registered when running as the real Windows service.
/// </summary>
public sealed class StopSignalService(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(
            () =>
            {
                using var signal = StopSignal.Create();
                if (WaitHandle.WaitAny([signal, stoppingToken.WaitHandle]) == 0)
                {
                    lifetime.StopApplication();
                }
            },
            CancellationToken.None);
}

/// <summary>Serves the desktop apps. If the pipe cannot be created, another instance already runs: stop.</summary>
public sealed class PipeHostedService(ProfileManager profiles, ILoggerFactory loggers) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        new PipeServer(IpcProtocol.PipeName, profiles.GetOrStartAsync, loggers.CreateLogger<PipeServer>()).RunAsync(stoppingToken);
}
