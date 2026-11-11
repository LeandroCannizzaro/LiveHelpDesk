using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

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
    private long _checkedAt = long.MinValue;
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
/// held back right now?". It turns the raw detectors into a decision the user governs with two independent switches,
/// and poses as an <see cref="IDoNotDisturb"/> so the overlay and the toast pump can share it unchanged.
/// </summary>
public sealed class SuppressionPolicy(UiSettings settings, IDoNotDisturb presentation, IVpnDetector vpn) : IDoNotDisturb
{
    public bool IsBusy() =>
        (settings.SuppressWhenPresenting && presentation.IsBusy()) ||
        (settings.SuppressWhenOnVpn && vpn.IsVpnActive());
}
