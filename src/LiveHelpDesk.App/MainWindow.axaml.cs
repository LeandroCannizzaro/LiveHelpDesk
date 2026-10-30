using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using LiveHelpDesk.Core;

namespace LiveHelpDesk.App;

/// <summary>
/// Phase 1 spike: hosts the Dynamics site in a WebView with a persistent profile, so the Entra sign-in
/// (cookies, SSO) survives restarts. Navigation events are appended to a log (host and path only, never
/// query strings) so the behaviour can be checked without looking at the window.
/// </summary>
public partial class MainWindow : Window
{
    // Default dashboard; will come from configuration (DynamicsOptions) in phase 6.
    private static readonly Uri Home = new(
        "https://qgs.crm4.dynamics.com/main.aspx?appid=d2ed1394-6c4e-ed11-bba3-00224899337b&pagetype=dashboard&id=2975aa11-0acc-ee11-9079-002248993a93&type=system&_canOverride=true");

    private static readonly string LogPath = Path.Combine(AppPaths.UserLogs, "webview-spike.log");

    public MainWindow()
    {
        InitializeComponent();

        Web.EnvironmentRequested += OnEnvironmentRequested;
        Web.NavigationStarted += (_, e) => Log($"start {Describe(e.Request)}");
        Web.NavigationCompleted += OnNavigationCompleted;
        Web.NewWindowRequested += OnNewWindowRequested;

        BackButton.Click += (_, _) => Web.GoBack();
        ForwardButton.Click += (_, _) => Web.GoForward();
        RefreshButton.Click += (_, _) => Web.Refresh();
        HomeButton.Click += (_, _) => Web.Navigate(Home);
        AddressBox.KeyDown += OnAddressKeyDown;

        Opened += (_, _) => Web.Navigate(Home);
        ScheduleAutoExit();
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        if (args is WindowsWebView2EnvironmentRequestedEventArgs webView2)
        {
            Directory.CreateDirectory(AppPaths.WebViewData);
            webView2.ProfileName = "LiveHelpDesk";
            webView2.UserDataFolder = AppPaths.WebViewData;
            // Sign in silently with the Windows primary (Entra) account when the PC has one.
            webView2.AllowSingleSignOnUsingOSPrimaryAccount = true;
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        Log($"done  success={e.IsSuccess} {Describe(e.Request)}");
        Dispatcher.UIThread.Post(() =>
        {
            AddressBox.Text = e.Request?.ToString();
            StatusText.Text = e.IsSuccess ? "Pronto" : "Caricamento non riuscito";
            BackButton.IsEnabled = Web.CanGoBack;
            ForwardButton.IsEnabled = Web.CanGoForward;
        });
    }

    // Links that would open a new window stay inside this view (single embedded browser).
    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { } uri)
        {
            Log($"newwindow {Describe(uri)}");
            Web.Navigate(uri);
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Uri.TryCreate(AddressBox.Text, UriKind.Absolute, out var uri))
        {
            Web.Navigate(uri);
        }
    }

    // --exit-after <seconds>: lets an unattended run (CI, smoke test) close the window by itself.
    private void ScheduleAutoExit()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--exit-after");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds))
        {
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(seconds));
        }
    }

    private static string Describe(Uri? uri) =>
        uri is null ? "(none)" : $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserLogs);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
