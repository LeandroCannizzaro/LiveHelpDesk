using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LiveHelpDesk.App.Services;

/// <summary>What the tray icon should look like.</summary>
public enum TrayLook
{
    /// <summary>No service, or not connected yet.</summary>
    Idle,

    Ok,

    Attention,

    /// <summary>Critical events waiting, or the monitor cannot do its job (sign-in needed, service down).</summary>
    Alert,
}

public static class TrayLooks
{
    public static TrayLook For(LinkState link, bool needsLogin, AttentionLevel attention) =>
        attention == AttentionLevel.Critical ? TrayLook.Alert
        : link == LinkState.ServiceMissing || needsLogin ? TrayLook.Alert
        : attention == AttentionLevel.Attention ? TrayLook.Attention
        : link == LinkState.Connected ? TrayLook.Ok
        : TrayLook.Idle;

    public static Color ColorOf(TrayLook look) => look switch
    {
        TrayLook.Ok => Color.Parse("#16A34A"),
        TrayLook.Attention => Color.Parse("#F59E0B"),
        TrayLook.Alert => Color.Parse("#DC2626"),
        _ => Color.Parse("#9CA3AF"),
    };
}

/// <summary>A round coloured badge with an "L", drawn on the fly: no icon files to ship, and the colour carries the state.</summary>
public static class TrayIconFactory
{
    public static WindowIcon Create(TrayLook look) => new(CreateBitmap(look));

    public static RenderTargetBitmap CreateBitmap(TrayLook look)
    {
        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(TrayLooks.ColorOf(look)),
            Child = new TextBlock
            {
                Text = "L",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 20,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };

        badge.Measure(new Size(32, 32));
        badge.Arrange(new Rect(0, 0, 32, 32));
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        bitmap.Render(badge);
        return bitmap;
    }
}

/// <summary>The icon in the notification area, with its menu. The app lives here: closing the window just hides it.</summary>
public sealed class TrayController
{
    private readonly TrayIcon _icon;
    private readonly Dictionary<TrayLook, WindowIcon> _icons = [];
    private TrayLook? _shown;

    public TrayController(Action open, Action acknowledgeAll, Action exit)
    {
        var menu = new NativeMenu();
        var openItem = new NativeMenuItem("Apri LiveHelpDesk");
        openItem.Click += (_, _) => open();
        var ackItem = new NativeMenuItem("Visti tutti");
        ackItem.Click += (_, _) => acknowledgeAll();
        var exitItem = new NativeMenuItem("Esci");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(openItem);
        menu.Items.Add(ackItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _icon = new TrayIcon { Menu = menu, ToolTipText = "LiveHelpDesk", IsVisible = true };
        _icon.Clicked += (_, _) => open();
    }

    public TrayIcon Icon => _icon;

    public void Update(TrayLook look, string tooltip)
    {
        if (_shown != look)
        {
            if (!_icons.TryGetValue(look, out var icon))
            {
                _icons[look] = icon = TrayIconFactory.Create(look);
            }

            _icon.Icon = icon;
            _shown = look;
        }

        _icon.ToolTipText = tooltip;
    }

    public void Dispose() => _icon.IsVisible = false;
}
