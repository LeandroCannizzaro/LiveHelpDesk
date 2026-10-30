using LiveHelpDesk.Core.Health;
using LiveHelpDesk.Core.Sources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>The loop: poll, publish, wait. Every failure ends up as a log line and, when it lasts, a health event.</summary>
public sealed class PollingWorker(Pipeline pipeline, ILogger<PollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LiveHelpDesk polling started (every {Seconds}s)", pipeline.Settings.PollInterval.TotalSeconds);
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await pipeline.EnsureMeAsync(stoppingToken);
                var result = await pipeline.Engine.PollOnceAsync(stoppingToken);

                failures = 0;
                var now = DateTimeOffset.UtcNow;
                var recovered = pipeline.Health.Success(now);
                if (recovered.Count > 0)
                {
                    pipeline.Engine.Publish(recovered, now);
                }

                if (result.Events.Count > 0 || result.BaselineTaken)
                {
                    logger.LogInformation(
                        "Poll done: {Events} events, {Queued} notifications queued{Baseline}",
                        result.Events.Count, result.Queued, result.BaselineTaken ? " (baseline)" : "");
                }

                delay = PollingSchedule.AfterSuccess(pipeline.Settings.PollInterval, Random.Shared.NextDouble());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (TicketSourceException ex)
            {
                failures++;
                logger.LogWarning("Poll failed ({Failure}): {Message}", ex.Failure, ex.Message);
                var now = DateTimeOffset.UtcNow;
                pipeline.Engine.Publish(pipeline.Health.Failure(ex, now), now);
                delay = PollingSchedule.AfterFailure(ex, failures);
            }
            catch (Exception ex)
            {
                // A bug or a full disk must not kill the monitor: report it like any other failure and carry on.
                failures++;
                logger.LogError(ex, "Unexpected error while polling");
                var failure = new TicketSourceException(SourceFailure.Error, ex.Message, inner: ex);
                var now = DateTimeOffset.UtcNow;
                pipeline.Engine.Publish(pipeline.Health.Failure(failure, now), now);
                delay = PollingSchedule.AfterFailure(failure, failures);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("LiveHelpDesk polling stopped");
    }
}
