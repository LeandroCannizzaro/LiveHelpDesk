namespace LiveHelpDesk.Service;

/// <summary>Placeholder worker: proves the hosting works. Replaced by the polling pipeline in phase 3.</summary>
public sealed class HeartbeatWorker(ILogger<HeartbeatWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LiveHelpDesk service started");
        using var timer = new PeriodicTimer(Interval);
        do
        {
            logger.LogInformation("Heartbeat {Time:O}", DateTimeOffset.Now);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
