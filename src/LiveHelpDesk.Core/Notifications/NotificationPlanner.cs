using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;

namespace LiveHelpDesk.Core.Notifications;

/// <summary>Turns "this rule matched this event" into outbox rows, one per channel.</summary>
/// <param name="policy">Read at every plan, so edits to quiet hours take effect at once.</param>
/// <param name="linkFor">Deep link to a ticket, when the source can build one.</param>
/// <param name="templates">User-written texts, read at every plan; a (rule, channel) without one gets the standard text.</param>
public sealed class NotificationPlanner(
    TicketMapping mapping,
    Func<ChannelPolicy> policy,
    Func<Guid, Uri?> linkFor,
    Func<IReadOnlyList<NotificationTemplate>>? templates = null)
{
    public IReadOnlyList<OutboxItem> Plan(RuleMatch match, DateTimeOffset now)
    {
        var rule = match.Rule;
        var evt = match.Event;
        var (title, body) = NotificationText.Describe(evt, mapping, rule.Actions.Detail);
        var url = evt.TicketId is { } id ? linkFor(id) : null;
        var standard = new Notification(title, body, rule.Actions.Severity, url);
        var custom = templates?.Invoke() ?? [];
        IReadOnlyDictionary<string, string?>? values = null;

        var items = new List<OutboxItem>();
        foreach (var channel in rule.Actions.Channels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            items.Add(Item(channel, notBefore: now, requiresUnacknowledged: false));
        }

        if (rule.Actions.EscalateAfterMinutes is > 0 and var minutes)
        {
            var later = now.AddMinutes(minutes);
            foreach (var channel in rule.Actions.EscalateChannels.Except(rule.Actions.Channels, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(Item(channel, notBefore: later, requiresUnacknowledged: true));
            }
        }

        return items;

        Notification For(string channel)
        {
            var template = custom.FirstOrDefault(t => t.Covers(rule.Id, channel));
            if (template is null)
            {
                return standard;
            }

            values ??= TemplateFields.Values(evt, mapping, string.IsNullOrWhiteSpace(rule.Name) ? rule.Id : rule.Name, rule.Actions.Severity, url);
            var customTitle = string.IsNullOrWhiteSpace(template.Title) ? "" : TemplateRenderer.RenderTitle(template.Title, values);
            var notification = standard with { Title = customTitle.Length == 0 ? standard.Title : customTitle };
            if (string.IsNullOrWhiteSpace(template.Body))
            {
                return notification;
            }

            return TemplateRenderer.FormatFor(channel) switch
            {
                TemplateFormat.Html => notification with
                {
                    Body = TemplateRenderer.RenderBody(template.Body, values, TemplateFormat.Plain),
                    HtmlBody = TemplateRenderer.RenderBody(template.Body, values, TemplateFormat.Html),
                },
                var format => notification with { Body = TemplateRenderer.RenderBody(template.Body, values, format) },
            };
        }

        OutboxItem Item(string channel, DateTimeOffset notBefore, bool requiresUnacknowledged) =>
            new(
                0,
                evt.DedupKey,
                rule.Id,
                channel,
                For(channel),
                notBefore,
                requiresUnacknowledged,
                policy().Allows(channel, rule.Actions.Severity, notBefore.ToLocalTime()) ? OutboxStatus.Pending : OutboxStatus.Suppressed,
                0,
                now);
    }
}
