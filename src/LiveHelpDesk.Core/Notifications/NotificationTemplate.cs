namespace LiveHelpDesk.Core.Notifications;

public sealed class TemplatesFile
{
    public int Version { get; set; } = TemplatesConfig.CurrentVersion;

    public List<NotificationTemplate> Templates { get; set; } = [];
}

/// <summary>
/// A user-written notification text for some rules on some channels. Every (rule, channel) pair it lists uses
/// this text instead of the standard one; a pair can belong to one template only.
/// </summary>
public sealed class NotificationTemplate
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<string> RuleIds { get; set; } = [];

    public List<string> Channels { get; set; } = [];

    /// <summary>One line of plain text with <c>{{campo}}</c> tags; empty means the standard title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Markdown with <c>{{campo}}</c> tags; empty means the standard text.</summary>
    public string Body { get; set; } = "";

    public bool Covers(string ruleId, string channel) =>
        RuleIds.Contains(ruleId, StringComparer.OrdinalIgnoreCase) && Channels.Contains(channel, StringComparer.OrdinalIgnoreCase);
}
