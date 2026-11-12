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

/// <summary>
/// A round badge with the application's bell (Phosphor Icons, MIT: see THIRD-PARTY-NOTICES.md), drawn on the fly:
/// the colour carries the state, so there is one icon per state without shipping any files.
/// </summary>
public static class TrayIconFactory
{
    private const string BellPath =
        "M224,71.1a8,8,0,0,1-10.78-3.42,94.13,94.13,0,0,0-33.46-36.91,8,8,0,1,1,8.54-13.54,111.46,111.46,0,0,1,39.12,43.09A8,8,0,0,1,224,71.1ZM35.71,72a8,8,0,0,0,7.1-4.32A94.13,94.13,0,0,1,76.27,30.77a8,8,0,1,0-8.54-13.54A111.46,111.46,0,0,0,28.61,60.32,8,8,0,0,0,35.71,72Zm186.1,103.94A16,16,0,0,1,208,200H167.2a40,40,0,0,1-78.4,0H48a16,16,0,0,1-13.79-24.06C43.22,160.39,48,138.28,48,112a80,80,0,0,1,160,0C208,138.27,212.78,160.38,221.81,175.94ZM150.62,200H105.38a24,24,0,0,0,45.24,0Z";

    public static WindowIcon Create(TrayLook look) => new(CreateBitmap(look));

    public static RenderTargetBitmap CreateBitmap(TrayLook look)
    {
        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(TrayLooks.ColorOf(look)),
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(BellPath),
                Fill = Brushes.White,
                Stretch = Stretch.Uniform,
                Width = 19,
                Height = 19,
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
