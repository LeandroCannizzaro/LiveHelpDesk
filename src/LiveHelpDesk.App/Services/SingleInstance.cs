namespace LiveHelpDesk.App.Services;

/// <summary>
/// One app per Windows session: a second launch (the Start menu, a toast click after a crash...) asks the first
/// one to come to the front and exits, instead of starting a second tray icon and a second overlay.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, EventWaitHandle show, Action onSecondLaunch)
    {
        _mutex = mutex;
        _show = show;
        Task.Run(() =>
        {
            var handles = new[] { _show, _stop.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                onSecondLaunch();
            }
        });
    }

    /// <summary>The running instance, or null when another one already runs (and has been asked to show itself).</summary>
    public static SingleInstance? TryAcquire(string name, Action onSecondLaunch)
    {
        // Not owned, only held open: the name exists while the first instance keeps the handle, and no thread must release it.
        var mutex = new Mutex(initiallyOwned: false, $"Local\\{name}.Mutex", out var first);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}.Show");
        if (first)
        {
            return new SingleInstance(mutex, show, onSecondLaunch);
        }

        show.Set();
        show.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.Dispose();
        _show.Dispose();
        _stop.Dispose();
    }
}
