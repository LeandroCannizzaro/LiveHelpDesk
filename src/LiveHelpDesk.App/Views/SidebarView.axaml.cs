using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace LiveHelpDesk.App.Views;

/// <summary>What the "Avvisi" button shows.</summary>
public enum AlertsState
{
    Active,

    /// <summary>Paused by the user.</summary>
    Paused,

    /// <summary>Held back automatically because the screen is shared.</summary>
    PausedForShare,
}

/// <summary>
/// The main window's side bar: the on-screen alerts switch, the panes, the update notice and the service state.
/// Expanded it shows icons with short labels; collapsed only the icons, whose tooltips say what they do.
/// </summary>
public partial class SidebarView : UserControl
{
    public const double ExpandedWidth = 190;
    public const double CollapsedWidth = 58;

    private readonly (ToggleButton Button, Pane Pane)[] _nav;
    private string _status = "";

    public SidebarView()
    {
        InitializeComponent();
        _nav =
        [
            (NavBrowser, Pane.Browser), (NavEvents, Pane.Events), (NavStatus, Pane.Status), (NavRules, Pane.Rules),
            (NavNotifications, Pane.Notifications), (NavChannels, Pane.Channels), (NavSettings, Pane.Settings),
        ];
        foreach (var (button, pane) in _nav)
        {
            button.Click += (_, _) => PaneRequested?.Invoke(pane);
        }

        UpdateButton.Click += (_, _) => PaneRequested?.Invoke(Pane.Settings);
        OverlayToggle.Click += (_, _) => OverlayToggleRequested?.Invoke();
        SidebarToggle.Click += (_, _) =>
        {
            SetCollapsed(!IsCollapsed);
            CollapsedChanged?.Invoke(IsCollapsed);
        };

        SetCollapsed(false);
        SetOverlayActive(true);
    }

    public bool IsCollapsed { get; private set; }

    public event Action<Pane>? PaneRequested;

    public event Action? OverlayToggleRequested;

    /// <summary>Raised when the user expands or collapses the bar, so the choice can be remembered.</summary>
    public event Action<bool>? CollapsedChanged;

    public void SetCollapsed(bool collapsed)
    {
        IsCollapsed = collapsed;
        Width = collapsed ? CollapsedWidth : ExpandedWidth;
        Sidebar.Classes.Set("collapsed", collapsed);
        foreach (var (button, _) in _nav)
        {
            ToolTip.SetServiceEnabled(button, collapsed);
        }

        // Expanded, the label already says it all.
        ToolTip.SetServiceEnabled(OverlayToggle, collapsed);

        UpdateToggleTip();
    }

    public void SetPane(Pane pane)
    {
        foreach (var (button, p) in _nav)
        {
            button.IsChecked = p == pane;
        }
    }

    /// <summary>The prominent switch that blocks or re-enables the on-screen alerts (overlay and Windows toasts): green when active, red when paused.</summary>
    public void SetOverlayActive(bool active) => SetAlerts(active ? AlertsState.Active : AlertsState.Paused);

    public void SetAlerts(AlertsState state)
    {
        var active = state == AlertsState.Active;
        OverlayToggleIcon.Text = active ? "🔔" : "🔕";
        OverlayToggleLabel.Text = state switch
        {
            AlertsState.Active => "Avvisi attivi",
            AlertsState.PausedForShare => "Avvisi sospesi per condivisione schermo",
            _ => "Avvisi in pausa",
        };
        OverlayToggle.Classes.Set("paused", !active);
        ToolTip.SetTip(OverlayToggle, state switch
        {
            AlertsState.Active => "Avvisi a video attivi. Clic per bloccare finestrella e notifiche di Windows.",
            AlertsState.PausedForShare => "Lo schermo è condiviso: gli avvisi restano sospesi fino al termine della condivisione. Clic per riattivarli.",
            _ => "Avvisi a video in pausa. Clic per riattivare finestrella e notifiche di Windows.",
        });
    }

    /// <summary>The service state under the panes, and the number of events waiting on the Eventi badge.</summary>
    public void SetStatus(string text, int waiting)
    {
        _status = text;
        LinkText.Text = text;
        EventsBadge.IsVisible = waiting > 0;
        EventsBadgeText.Text = waiting > 99 ? "99+" : waiting.ToString();
        ToolTip.SetTip(NavEvents, waiting > 0 ? $"Eventi: {waiting} da gestire" : "Eventi: gli ultimi avvisi ricevuti");
        UpdateToggleTip();
    }

    /// <summary>The "new version" button; null hides it.</summary>
    public void SetUpdateAvailable(string? version)
    {
        UpdateButton.IsVisible = version is not null;
        UpdateLabel.Text = $"Versione {version}";
        ToolTip.SetTip(UpdateButton, $"È disponibile la versione {version}: apri Impostazioni → Aggiornamenti");
    }

    // Collapsed, the service state text is hidden: the toggle's tooltip carries it instead.
    private void UpdateToggleTip() =>
        ToolTip.SetTip(SidebarToggle, IsCollapsed
            ? (_status.Length > 0 ? $"Espandi la barra · {_status}" : "Espandi la barra")
            : "Riduci la barra alle sole icone");
}
