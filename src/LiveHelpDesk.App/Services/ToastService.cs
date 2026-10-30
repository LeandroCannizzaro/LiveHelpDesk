using CommunityToolkit.WinUI.Notifications;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.Services;

/// <param name="Action">"open" or "acknowledge".</param>
public sealed record ToastAction(string Action, string EventKey, Uri? Url);

/// <summary>Windows toasts with "Apri" and "Riconosci" buttons. What a click means travels in the toast's own arguments.</summary>
public sealed class ToastService : IToastPresenter
{
    public const string OpenAction = "open";
    public const string AcknowledgeAction = "acknowledge";

    /// <summary>Call once at startup: toasts can be clicked long after they were shown.</summary>
    public static void Listen(Action<ToastAction> onAction) =>
        ToastNotificationManagerCompat.OnActivated += activated =>
        {
            var action = Parse(activated.Argument);
            if (action is not null)
            {
                onAction(action);
            }
        };

    public void Show(NotificationInfo notification)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("key", notification.EventKey)
            .AddArgument("action", OpenAction) // a click on the toast itself opens the ticket
            .AddText(notification.Title)
            .AddText(string.IsNullOrWhiteSpace(notification.Body) ? " " : notification.Body);

        if (notification.Url is not null)
        {
            builder.AddArgument("url", notification.Url.ToString());
        }

        builder
            .AddButton(new ToastButton()
                .SetContent("Apri")
                .AddArgument("action", OpenAction)
                .AddArgument("key", notification.EventKey)
                .AddArgument("url", notification.Url?.ToString() ?? ""))
            .AddButton(new ToastButton()
                .SetContent("Riconosci")
                .AddArgument("action", AcknowledgeAction)
                .AddArgument("key", notification.EventKey)
                .SetBackgroundActivation());

        if (notification.Severity == Severity.Critical)
        {
            // Stays on screen until dealt with, instead of vanishing after a few seconds.
            builder.SetToastScenario(ToastScenario.Reminder);
        }

        builder.Show();
    }

    /// <summary>Reads the arguments of a clicked toast; null when they are not ours.</summary>
    public static ToastAction? Parse(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return null;
        }

        var parsed = ToastArguments.Parse(arguments);
        if (!parsed.TryGetValue("action", out var action) || !parsed.TryGetValue("key", out var key))
        {
            return null;
        }

        Uri? url = parsed.TryGetValue("url", out var text) && Uri.TryCreate(text, UriKind.Absolute, out var parsedUrl) ? parsedUrl : null;
        return new ToastAction(action, key, url);
    }
}
