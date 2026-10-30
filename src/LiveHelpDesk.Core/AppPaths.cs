namespace LiveHelpDesk.Core;

/// <summary>Where LiveHelpDesk keeps its files. Machine-wide data belongs to the service, UI data to the user.</summary>
public static class AppPaths
{
    private const string Folder = "LiveHelpDesk";

    /// <summary>Service data: configuration, per-user profiles, logs. <c>LIVEHELPDESK_DATA</c> overrides it (development, tests).</summary>
    public static string MachineRoot { get; } =
        Environment.GetEnvironmentVariable("LIVEHELPDESK_DATA") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Folder);

    /// <summary>Per-user UI data: window state, WebView profile, UI log.</summary>
    public static string UserRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);

    /// <summary>Persistent WebView2 user-data folder, so the Dynamics sign-in survives restarts.</summary>
    public static string WebViewData => Path.Combine(UserRoot, "webview");

    public static string UserLogs => Path.Combine(UserRoot, "logs");

    public static string ProfileDirectory(string windowsSid) => Path.Combine(MachineRoot, "profiles", windowsSid);
}
