using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.Core;

namespace LiveHelpDesk.App;

public enum Pane
{
    Browser,
    Events,
    Status,
    Rules,
    Notifications,
    Channels,
    Settings,
}

/// <summary>
/// The window the user opens from the tray: Dynamics in a WebView, the recent events, and the service's state.
/// Closing it only hides it, so the browser session (and the Dynamics sign-in) stays alive; the app really
/// ends from the tray menu.
/// </summary>
public partial class MainWindow : Window
{
    // Used until the service tells us its own dashboard address.
    private static readonly Uri DefaultHome = new(
        "https://qgs.crm4.dynamics.com/main.aspx?appid=d2ed1394-6c4e-ed11-bba3-00224899337b&pagetype=dashboard&id=2975aa11-0acc-ee11-9079-002248993a93&type=system&_canOverride=true");

    private static readonly string LogPath = Path.Combine(AppPaths.UserLogs, "webview-spike.log");

    private Uri _home = DefaultHome;
    private bool _userNavigated;

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
        HomeButton.Click += (_, _) => Navigate(_home);
        AddressBox.KeyDown += OnAddressKeyDown;

        NavBrowser.Click += (_, _) => ShowPane(Pane.Browser);
        NavEvents.Click += (_, _) => ShowPane(Pane.Events);
        NavStatus.Click += (_, _) => ShowPane(Pane.Status);
        NavRules.Click += (_, _) => ShowPane(Pane.Rules);
        NavNotifications.Click += (_, _) => ShowPane(Pane.Notifications);
        NavChannels.Click += (_, _) => ShowPane(Pane.Channels);
        NavSettings.Click += (_, _) => ShowPane(Pane.Settings);

        Opened += (_, _) => Web.Navigate(_home);
        Closing += (_, e) =>
        {
            if (!ReallyClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>Set by the tray's "Esci": only then does closing the window end the app.</summary>
    public bool ReallyClose { get; set; }

    public Pane CurrentPane { get; private set; } = Pane.Browser;

    /// <summary>Raised when a pane is shown, so it can refresh what it displays.</summary>
    public event Action<Pane>? PaneChanged;

    public void Attach(
        EventsViewModel events, StatusViewModel status, RulesViewModel rules, NotificationsViewModel notifications,
        ChannelsViewModel channels, SettingsViewModel settings)
    {
        EventsPane.DataContext = events;
        StatusPane.DataContext = status;
        RulesPane.DataContext = rules;
        NotificationsPane.DataContext = notifications;
        ChannelsPane.DataContext = channels;
        SettingsPane.DataContext = settings;
    }

    /// <summary>The screens Windows lists, for the overlay's screen choice.</summary>
    public IReadOnlyList<string> ScreenNames() =>
        [.. Screens.All.Select((screen, i) => $"Schermo {i + 1}{(screen.IsPrimary ? " (principale)" : "")} · {screen.Bounds.Width}×{screen.Bounds.Height}")];

    /// <summary>The dashboard the service wants as the starting page. Follows only if the user has not moved on.</summary>
    public void SetHome(Uri home)
    {
        if (home == _home)
        {
            return;
        }

        var wasOnHome = !_userNavigated;
        _home = home;
        if (wasOnHome)
        {
            Navigate(home);
        }
    }

    public void ShowPane(Pane pane)
    {
        CurrentPane = pane;
        BrowserPane.IsVisible = pane == Pane.Browser;
        EventsPane.IsVisible = pane == Pane.Events;
        StatusPane.IsVisible = pane == Pane.Status;
        RulesPane.IsVisible = pane == Pane.Rules;
        NotificationsPane.IsVisible = pane == Pane.Notifications;
        ChannelsPane.IsVisible = pane == Pane.Channels;
        SettingsPane.IsVisible = pane == Pane.Settings;
        NavBrowser.IsChecked = pane == Pane.Browser;
        NavEvents.IsChecked = pane == Pane.Events;
        NavStatus.IsChecked = pane == Pane.Status;
        NavRules.IsChecked = pane == Pane.Rules;
        NavNotifications.IsChecked = pane == Pane.Notifications;
        NavChannels.IsChecked = pane == Pane.Channels;
        NavSettings.IsChecked = pane == Pane.Settings;
        PaneChanged?.Invoke(pane);
    }

    /// <summary>Brings the window to the front, from the tray, a toast or a second launch.</summary>
    public void Bring()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // A brief topmost flip pulls the window above the others; Activate alone can lose to foreground rules.
        Topmost = true;
        Topmost = false;
        Activate();
    }

    public void OpenTicket(Uri url)
    {
        ShowPane(Pane.Browser);
        Navigate(url);
        Bring();
    }

    public void SetLinkText(string text, int waiting)
    {
        LinkText.Text = text;
        NavEvents.Content = waiting > 0 ? $"Eventi ({waiting})" : "Eventi";
    }

    private void Navigate(Uri url)
    {
        _userNavigated = url != _home;
        Web.Navigate(url);
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
            Navigate(uri);
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
