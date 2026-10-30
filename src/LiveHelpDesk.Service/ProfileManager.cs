using System.Collections.Concurrent;
using LiveHelpDesk.Ipc;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>
/// The service's table of users: one running monitor per Windows identity, started the first time that
/// identity shows up (or at startup, if it has signed in before). Nobody can reach another user's monitor.
/// </summary>
public sealed class ProfileManager(Func<string, Task<IProfile>> create, ILogger logger) : IAsyncDisposable
{
    private sealed record Running(IProfile Profile, Task Loop);

    private readonly ConcurrentDictionary<string, Lazy<Task<Running>>> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();

    public IReadOnlyCollection<string> Sids => [.. _profiles.Keys];

    public async Task<ILiveHelpDeskApi> GetOrStartAsync(string sid, CancellationToken cancellationToken)
    {
        // Lazy makes two simultaneous first connections of the same user create one monitor, not two.
        var lazy = _profiles.GetOrAdd(sid, key => new Lazy<Task<Running>>(() => StartAsync(key)));
        try
        {
            return (await lazy.Value.WaitAsync(cancellationToken)).Profile;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _profiles.TryRemove(new KeyValuePair<string, Lazy<Task<Running>>>(sid, lazy)); // let the next call try again
            throw;
        }
    }

    public async Task StartExistingAsync(IEnumerable<string> sids)
    {
        foreach (var sid in sids)
        {
            try
            {
                await GetOrStartAsync(sid, _stopping.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start the monitor of {Sid}", sid);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var lazy in _profiles.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            try
            {
                var running = await lazy.Value;
                await running.Loop.WaitAsync(TimeSpan.FromSeconds(10));
                await running.Profile.DisposeAsync();
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                logger.LogWarning("A monitor did not stop in time");
            }
        }

        _stopping.Dispose();
    }

    private async Task<Running> StartAsync(string sid)
    {
        var profile = await create(sid);
        var loop = Task.Run(async () =>
        {
            try
            {
                await profile.RunAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The monitor of {Sid} stopped unexpectedly", sid);
            }
        });
        logger.LogInformation("Monitor for {Sid} started", sid);
        return new Running(profile, loop);
    }
}
