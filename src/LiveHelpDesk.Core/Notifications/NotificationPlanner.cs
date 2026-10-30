using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;

namespace LiveHelpDesk.Core.Notifications;

/// <summary>Turns "this rule matched this event" into outbox rows, one per channel.</summary>
/// <param name="linkFor">Deep link to a ticket, when the source can build one.</param>
public sealed class NotificationPlanner(TicketMapping mapping, ChannelPolicy policy, Func<Guid, Uri?> linkFor)
{
    public IReadOnlyList<OutboxItem> Plan(RuleMatch match, DateTimeOffset now)
    {
        var rule = match.Rule;
        var evt = match.Event;
        var (title, body) = NotificationText.Describe(evt, mapping, rule.Actions.Detail);
        var url = evt.TicketId is { } id ? linkFor(id) : null;
        var notification = new Notification(title, body, rule.Actions.Severity, url);
        var local = now.ToLocalTime();

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

        OutboxItem Item(string channel, DateTimeOffset notBefore, bool requiresUnacknowledged) =>
            new(
                0,
                evt.DedupKey,
                rule.Id,
                channel,
                notification,
                notBefore,
                requiresUnacknowledged,
                policy.Allows(channel, rule.Actions.Severity, notBefore.ToLocalTime()) ? OutboxStatus.Pending : OutboxStatus.Suppressed,
                0,
                now);
    }
}
