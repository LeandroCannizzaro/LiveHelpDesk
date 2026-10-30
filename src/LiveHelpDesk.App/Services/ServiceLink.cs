using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.Services;

public enum LinkState
{
    /// <summary>Not connected yet (just started, or reconnecting).</summary>
    Connecting,

    Connected,

    /// <summary>The service did not answer: not running or not installed. Keeps trying quietly.</summary>
    ServiceMissing,
}

/// <summary>
/// The app's line to the service. It stays up by itself: whenever the connection drops (service restarted,
/// updated, crashed) it reconnects with a growing pause, so the app never needs a restart to recover.
/// </summary>
public sealed class ServiceLink : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly TimeSpan _firstRetry;
    private readonly TimeSpan _longestRetry;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private IpcConnection? _connection;

    public ServiceLink(string pipeName, TimeSpan? firstRetry = null, TimeSpan? longestRetry = null)
    {
        _pipeName = pipeName;
        _firstRetry = firstRetry ?? TimeSpan.FromSeconds(1);
        _longestRetry = longestRetry ?? TimeSpan.FromSeconds(15);
    }

    public LinkState State { get; private set; } = LinkState.Connecting;

    /// <summary>The service's API while connected, otherwise null.</summary>
    public ILiveHelpDeskApi? Api => _connection?.Api;

    public event Action<LinkState>? StateChanged;

    /// <summary>Forwarded from the service (about this user's profile only).</summary>
    public event EventHandler<ChangedEventArgs>? Changed;

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var wait = _firstRetry;
        while (!_stop.IsCancellationRequested)
        {
            IpcConnection? connection = null;
            try
            {
                connection = await IpcClient.TryConnectAsync(_pipeName, TimeSpan.FromSeconds(2), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (connection is null)
            {
                Set(State == LinkState.Connected ? LinkState.Connecting : LinkState.ServiceMissing);
                if (!await Delay(wait))
                {
                    return;
                }

                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, _longestRetry.Ticks));
                continue;
            }

            wait = _firstRetry;
            _connection = connection;
            connection.Api.Changed += OnChanged;
            Set(LinkState.Connected);

            try
            {
                await connection.Disconnected.WaitAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // The connection broke: fall through and reconnect.
            }
            finally
            {
                connection.Api.Changed -= OnChanged;
                _connection = null;
                await connection.DisposeAsync();
            }

            Set(LinkState.Connecting);
        }
    }

    private void OnChanged(object? sender, ChangedEventArgs e) => Changed?.Invoke(this, e);

    private void Set(LinkState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }
    }

    private async Task<bool> Delay(TimeSpan time)
    {
        try
        {
            await Task.Delay(time, _stop.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}
