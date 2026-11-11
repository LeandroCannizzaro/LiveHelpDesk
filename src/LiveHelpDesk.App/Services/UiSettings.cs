using System.Text.Json;
using LiveHelpDesk.Core;

namespace LiveHelpDesk.App.Services;

/// <summary>This user's look-and-feel choices, kept next to the WebView profile. A missing or damaged file means defaults.</summary>
public sealed class UiSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool OverlayEnabled { get; set; } = true;

    /// <summary>Sideways shift from the centre of the screen, in device-independent pixels (set by dragging the overlay).</summary>
    public double OverlayOffsetX { get; set; }

    /// <summary>Distance from the top of the work area.</summary>
    public double OverlayTop { get; set; } = 8;

    public bool PlaySoundForCritical { get; set; } = true;

    /// <summary>Hold back the overlay and Windows toasts while a presentation, screen share, or full-screen app is active.</summary>
    public bool SuppressWhenPresenting { get; set; } = true;

    /// <summary>Hold back the overlay and Windows toasts while a VPN / tunnel connection is up (an untrusted network).</summary>
    public bool SuppressWhenOnVpn { get; set; }

    /// <summary>Which screen shows the overlay: 0 = the main one, N = the Nth screen Windows lists, <see cref="AllScreens"/> = every screen.</summary>
    public int OverlayScreen { get; set; }

    public const int AllScreens = -1;

    /// <summary>Overlay background colour as "#RRGGBB". Blank = the urgency default (amber for attention, red for critical).</summary>
    public string OverlayBackground { get; set; } = "";

    /// <summary>Overlay text colour as "#RRGGBB". Blank = the urgency default.</summary>
    public string OverlayTextColor { get; set; } = "";

    /// <summary>Overlay accent colour (the number badge) as "#RRGGBB". Blank = a subtle translucent black.</summary>
    public string OverlayAccent { get; set; } = "";

    /// <summary>When no service answers, the app starts the monitor itself (<c>LiveHelpDesk.Service run</c>).</summary>
    public bool StartMonitorProcess { get; set; } = true;

    /// <summary>The main window's side bar shows only icons.</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>The last version announced in the overlay, so each new version is announced once.</summary>
    public string AnnouncedUpdate { get; set; } = "";

    public static string DefaultPath => Path.Combine(AppPaths.UserRoot, "ui.json");

    public static UiSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path), Json) ?? new UiSettings() : new UiSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UiSettings();
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a window position is not worth an error.
        }
    }
}
