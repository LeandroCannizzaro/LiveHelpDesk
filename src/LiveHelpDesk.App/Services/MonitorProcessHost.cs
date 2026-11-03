using System.Diagnostics;

namespace LiveHelpDesk.App.Services;

/// <summary>A running <c>LiveHelpDesk.Service run</c> as the host sees it; faked in tests.</summary>
public interface IMonitoredProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code when the process ends, however it ends.</summary>
    Task<int> Exited { get; }

    void Kill();
}

public enum MonitorProcessState
{
    Stopped,
    Running,

    /// <summary>It ended by itself and is about to be started again.</summary>
    Restarting,

    /// <summary>It kept ending (or could not start): the host gave up until asked again.</summary>
    Failed,
}

/// <summary>
/// Runs the monitor as a process of this app (the service's <c>run</c> command, no administrator needed) and watches
/// it: if it ends without being asked to, it is started again a few times, with growing pauses, and then reported as failed.
/// Stopping asks it to end cleanly through <see cref="Ipc.StopSignal"/> and only kills it if it does not.
/// </summary>
public sealed class MonitorProcessHost : IAsyncDisposable
{
    private static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly Func<IMonitoredProcess> _start;
    private readonly Func<bool> _raiseStop;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly TimeSpan _stableAfter;
    private readonly int _stopAttempts;
    private readonly TimeSpan _stopPoll;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    private CancellationTokenSource? _wanted;
    private Task? _loop;
    private IMonitoredProcess? _current;

    public MonitorProcessHost(
        Func<IMonitoredProcess> start,
        Func<bool> raiseStop,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IReadOnlyList<TimeSpan>? backoff = null,
        TimeSpan? stableAfter = null,
        int stopAttempts = 20,
        TimeSpan? stopPoll = null,
        Func<DateTimeOffset>? now = null)
    {
        _start = start;
        _raiseStop = raiseStop;
        _delay = delay ?? Task.Delay;
        _backoff = backoff ?? DefaultBackoff;
        _stableAfter = stableAfter ?? TimeSpan.FromMinutes(1);
        _stopAttempts = stopAttempts;
        _stopPoll = stopPoll ?? TimeSpan.FromMilliseconds(500);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public MonitorProcessState State { get; private set; } = MonitorProcessState.Stopped;

    public int? ProcessId { get; private set; }

    public int? LastExitCode { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>True while this host wants the monitor running (it may be between two restarts).</summary>
    public bool IsActive => State is MonitorProcessState.Running or MonitorProcessState.Restarting;

    /// <summary>Raised from a background thread.</summary>
    public event Action? Changed;

    /// <summary>Starts the monitor and returns once the first launch has been attempted. Does nothing if it is already active.</summary>
    public Task StartAsync()
    {
        TaskCompletionSource firstLaunch;
        CancellationToken token;
        lock (_gate)
        {
            if (_wanted is not null && _loop is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _wanted?.Dispose();
            _wanted = new CancellationTokenSource();
            token = _wanted.Token;
            firstLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LastError = null;
            LastExitCode = null;
            _loop = Task.Run(() => SuperviseAsync(firstLaunch, token), CancellationToken.None);
        }

        return firstLaunch.Task;
    }

    /// <summary>Asks the monitor this host started to end, cleanly if it can, and waits for it.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? wanted;
        Task? loop;
        IMonitoredProcess? current;
        lock (_gate)
        {
            wanted = _wanted;
            loop = _loop;
            current = _current;
            _wanted = null;
        }

        if (wanted is null)
        {
            return;
        }

        await wanted.CancelAsync();
        if (current is not null)
        {
            for (var attempt = 0; attempt < _stopAttempts && !current.Exited.IsCompleted; attempt++)
            {
                // Repeated: the monitor may not have created the signal yet if it was started a moment ago.
                _raiseStop();
                await Task.WhenAny(current.Exited, _delay(_stopPoll, CancellationToken.None));
            }

            if (!current.Exited.IsCompleted)
            {
                current.Kill();
            }
        }

        if (loop is not null)
        {
            await loop;
        }

        wanted.Dispose();
        Set(MonitorProcessState.Stopped);
    }

    /// <summary>For a monitor this host did not start (an earlier copy of the app did): asks it to end. False if nobody listens.</summary>
    public bool SignalStop() => _raiseStop();

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task SuperviseAsync(TaskCompletionSource firstLaunch, CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            IMonitoredProcess process;
            try
            {
                process = _start();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Set(MonitorProcessState.Failed);
                firstLaunch.TrySetResult();
                return;
            }

            var startedAt = _now();
            lock (_gate)
            {
                _current = process;
            }

            ProcessId = process.Id;
            Set(MonitorProcessState.Running);
            firstLaunch.TrySetResult();

            var code = await process.Exited;
            lock (_gate)
            {
                _current = null;
            }

            process.Dispose();
            ProcessId = null;
            LastExitCode = code;
            if (token.IsCancellationRequested)
            {
                break;
            }

            if (_now() - startedAt >= _stableAfter)
            {
                failures = 0;
            }

            if (failures >= _backoff.Count)
            {
                LastError = $"si è fermato {failures + 1} volte di seguito (ultimo codice {code})";
                Set(MonitorProcessState.Failed);
                return;
            }

            var pause = _backoff[failures++];
            Set(MonitorProcessState.Restarting);
            try
            {
                await _delay(pause, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Set(MonitorProcessState.Stopped);
    }

    private void Set(MonitorProcessState state)
    {
        State = state;
        Changed?.Invoke();
    }
}

/// <summary>The real thing: <c>LiveHelpDesk.Service.exe run</c>, hidden, under the user who runs the app.</summary>
public sealed class MonitorProcess : IMonitoredProcess
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MonitorProcess(Process process)
    {
        _process = process;
    }

    public int Id { get; private set; }

    public Task<int> Exited => _exited.Task;

    public static MonitorProcess Start(string executablePath)
    {
        var info = new ProcessStartInfo(executablePath, "run")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        };
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var monitor = new MonitorProcess(process);
        process.Exited += (_, _) => monitor._exited.TrySetResult(SafeExitCode(process));
        process.Start();
        monitor.Id = process.Id;
        return monitor;
    }

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose() => _process.Dispose();

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
