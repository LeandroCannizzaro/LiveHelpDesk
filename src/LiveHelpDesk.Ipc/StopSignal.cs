namespace LiveHelpDesk.Ipc;

/// <summary>
/// How the app asks a monitor it started (<c>LiveHelpDesk.Service run</c>) to end gracefully: a named event in the
/// user's session. The real Windows service lives in session 0 and never listens, so no user can stop it this way.
/// </summary>
public static class StopSignal
{
    public const string DefaultName = @"Local\LiveHelpDesk.Service.Stop";

    /// <summary>The monitor's side: hold this and wait on it.</summary>
    public static EventWaitHandle Create(string name = DefaultName) => new(false, EventResetMode.AutoReset, name);

    /// <summary>The app's side. False when nobody is listening.</summary>
    public static bool Raise(string name = DefaultName)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }
}
