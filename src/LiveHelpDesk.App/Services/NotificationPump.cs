using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.Services;

/// <summary>Shows a notification as a Windows toast.</summary>
public interface IToastPresenter
{
    void Show(NotificationInfo notification);
}

/// <summary>
/// Collects what the service has queued for this screen. "toast" items become Windows toasts, "overlay" items
/// go to the attention model that the overlay and the tray icon follow. When <paramref name="suppression"/> says the
/// moment is wrong (a presentation, an untrusted network), a toast is held back and routed to the attention model
/// instead, so nothing pops up uncontrolled on screen yet nothing is lost either.
/// </summary>
public sealed class NotificationPump(
    ServiceLink link, AttentionModel attention, IToastPresenter toasts, Func<DateTimeOffset> clock, IDoNotDisturb? suppression = null)
{
    public const string ToastChannel = "toast";
    public const string OverlayChannel = "overlay";

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <returns>How many notifications were collected.</returns>
    public async Task<int> PumpAsync()
    {
        await _one.WaitAsync();
        try
        {
            if (link.Api is not { } api)
            {
                return 0;
            }

            var claimed = await api.ClaimNotificationsAsync([ToastChannel, OverlayChannel]);
            foreach (var item in claimed)
            {
                Deliver(item);
            }

            return claimed.Count;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Shows something right now without going through the service (used by the demo switch).</summary>
    public void Deliver(NotificationInfo item)
    {
        if (item.Channel.Equals(ToastChannel, StringComparison.OrdinalIgnoreCase))
        {
            if (suppression?.IsBusy() == true)
            {
                // Hold the Windows toast back, but keep the information by routing it to the overlay (which is itself
                // suppressed meanwhile); it surfaces quietly once the presentation ends or the VPN drops.
                attention.Add(new AttentionItem(item.EventKey, item.Title, item.Body, item.Severity, item.Url, clock()), clock());
                return;
            }

            toasts.Show(item);
        }
        else if (item.Channel.Equals(OverlayChannel, StringComparison.OrdinalIgnoreCase))
        {
            attention.Add(new AttentionItem(item.EventKey, item.Title, item.Body, item.Severity, item.Url, clock()), clock());
        }
    }
}
