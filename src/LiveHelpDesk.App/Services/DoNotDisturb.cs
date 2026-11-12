using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiveHelpDesk.App.Services;

/// <summary>Is the user in the middle of something that must not be covered (a presentation, a full-screen game)?</summary>
public interface IDoNotDisturb
{
    bool IsBusy();
}

public sealed class NeverBusy : IDoNotDisturb
{
    public bool IsBusy() => false;
}

public sealed class WindowsDoNotDisturb : IDoNotDisturb
{
    // QUERY_USER_NOTIFICATION_STATE
    private const int Busy = 2;
    private const int FullScreen = 3;
    private const int Presentation = 4;

    public bool IsBusy() => IsBusyState(Query());

    public static bool IsBusyState(int state) => state is Busy or FullScreen or Presentation;

    private static int Query() => SHQueryUserNotificationState(out var state) == 0 ? state : 5; // 5 = accepts notifications

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>
/// Is the screen being shared right now?
/// other apps (Meet in a browser, a recording) by the privacy "ConsentStore", where Windows keeps each app's last
/// screen-capture start and stop: a start with no later stop means it is still going. Cached briefly so it can be polled.
/// </summary>
public sealed class WindowsScreenShareDetector(Func<DateTime>? utcNow = null) : IDoNotDisturb
{
    private static readonly string[] Capabilities = ["graphicsCaptureWithoutBorder", "graphicsCaptureProgrammatic"];
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const long TtlMilliseconds = 1000;
    private const string ShareBorderClass = "ScreenBorderWindow";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    /// <summary>An app that crashed mid-capture never writes its stop time: such an old "still capturing" entry is ignored.</summary>
    public static readonly TimeSpan MaxShareAge = TimeSpan.FromHours(12);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private long _checkedAt = -TtlMilliseconds; // already stale at the first call
    private bool _last;

    public bool IsBusy()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _checkedAt >= TtlMilliseconds)
            {
                _checkedAt = now;
                _last = Probe();
            }

            return _last;
        }
    }

    /// <param name="start">LastUsedTimeStart, a FILETIME.</param>
    /// <param name="stop">LastUsedTimeStop, a FILETIME; 0 or older than the start while the capture is going.</param>
    public static bool IsCapturing(long start, long stop, DateTime utcNow)
    {
        if (start <= 0 || stop >= start)
        {
            return false;
        }

        try
        {
            return utcNow - DateTime.FromFileTimeUtc(start) <= MaxShareAge;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private bool Probe() => HasShareBorderWindow() || HasLiveCaptureEntry();

    /// <summary>
    /// Teams does not use the capture API Windows keeps a record of: while it shares, it draws a full-screen
    /// "ScreenBorderWindow" around what is shared. It is language independent and goes away when the share ends.
    /// </summary>
    private static bool HasShareBorderWindow()
    {
        var after = IntPtr.Zero;
        while ((after = FindWindowEx(IntPtr.Zero, after, ShareBorderClass, null)) != IntPtr.Zero)
        {
            if (IsWindowVisible(after))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasLiveCaptureEntry()
    {
        try
        {
            var now = _utcNow();
            foreach (var capability in Capabilities)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{capability}");
                if (key is not null && AnyCapturing(key, now))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Can't tell: assume nobody is sharing rather than block notifications forever.
        }

        return false;
    }

    // Packaged apps (new Teams) are a sub-key of the capability; classic apps are one level lower, under "NonPackaged".
    private static bool AnyCapturing(RegistryKey key, DateTime now)
    {
        if (key.GetValue("LastUsedTimeStart") is long start && IsCapturing(start, key.GetValue("LastUsedTimeStop") is long stop ? stop : 0, now))
        {
            return true;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub is not null && AnyCapturing(sub, now))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Is a VPN / tunnel connection currently up? A signal that this is "not your own" network.</summary>
public interface IVpnDetector
{
    bool IsVpnActive();
}

public sealed class NoVpn : IVpnDetector
{
    public bool IsVpnActive() => false;
}

/// <summary>
/// Spots a live VPN by looking at the network adapters that are up: a tunnel-type adapter, or one whose name or
/// description matches a known VPN client (including the Windows "WAN Miniport" adapters used by L2TP/IKEv2/SSTP).
/// There is no single VPN API, so this is a best-effort heuristic; the result is cached briefly so it can be polled.
/// </summary>
public sealed class WindowsVpnDetector : IVpnDetector
{
    // Lowercase substrings seen in adapter Name/Description for common VPN clients and the Windows built-in VPN.
    private static readonly string[] Markers =
    [
        "vpn", "wireguard", "openvpn", "anyconnect", "cisco", "fortinet", "forticlient", "fortissl",
        "pulse secure", "globalprotect", "palo alto", "zscaler", "wan miniport", "nordlynx", "nordvpn",
        "expressvpn", "tap-windows", "tunnel", "tailscale", "zerotier", "sonicwall", "netextender", "check point",
    ];

    private const long TtlMilliseconds = 3000;
    private readonly object _gate = new();
    private long _checkedAt = -TtlMilliseconds; // already stale at the first call (long.MinValue would overflow the subtraction)
    private bool _last;

    public bool IsVpnActive()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _checkedAt < TtlMilliseconds)
            {
                return _last;
            }

            _checkedAt = now;
            _last = Probe();
            return _last;
        }
    }

    private static bool Probe()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    return true;
                }

                var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                if (Array.Exists(Markers, m => text.Contains(m)))
                {
                    return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Can't tell: assume the network is fine rather than block notifications forever.
        }

        return false;
    }
}

/// <summary>
/// The single answer to "should the uncontrolled, on-screen notifications (the overlay pill and Windows toasts) be
/// held back right now?". Yes while the user has paused the alerts (the "Avvisi in pausa" button), while the screen
/// is shared (unless the user reactivated the alerts for that share, see <see cref="ScreenShareGuard"/>), and, governed
/// by two independent switches, during a presentation or on a VPN. It poses as an <see cref="IDoNotDisturb"/> so the
/// overlay and the toast pump can share it unchanged.
/// </summary>
public sealed class SuppressionPolicy(
    UiSettings settings, IDoNotDisturb presentation, IVpnDetector vpn, ScreenShareGuard? screenShare = null) : IDoNotDisturb
{
    public bool IsBusy() =>
        !settings.OverlayEnabled ||
        screenShare?.IsPaused == true ||
        (settings.SuppressWhenPresenting && screenShare?.IsReactivated != true && presentation.IsBusy()) ||
        (settings.SuppressWhenOnVpn && vpn.IsVpnActive());
}
