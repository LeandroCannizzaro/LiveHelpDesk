using System.Text.Json.Nodes;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

public enum DetailLevel
{
    /// <summary>Everything the channel can carry.</summary>
    Full,

    /// <summary>Ticket number, priority and link only (for channels that leave the company, e.g. SMS/WhatsApp).</summary>
    Minimal,
}

public sealed class RulesFile
{
    public int Version { get; set; } = RulesConfig.CurrentVersion;

    public List<Rule> Rules { get; set; } = [];
}

/// <summary>A rule = events + condition + actions. It never means "mine" or "global" by itself: filters say that.</summary>
public sealed class Rule
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public List<TicketEventType> Events { get; set; } = [];

    public RuleCondition? When { get; set; }

    public RuleActions Actions { get; set; } = new();
}

public sealed class RuleActions
{
    public List<string> Channels { get; set; } = [];

    public Severity Severity { get; set; } = Severity.Info;

    public DetailLevel Detail { get; set; } = DetailLevel.Full;

    /// <summary>When set, channels in <see cref="EscalateChannels"/> are used if nobody acknowledges in time.</summary>
    public int? EscalateAfterMinutes { get; set; }

    public List<string> EscalateChannels { get; set; } = [];
}

/// <summary>
/// One node of the condition tree. A leaf has <see cref="Field"/> + <see cref="Op"/> (+ <see cref="Value"/>);
/// a group has <see cref="All"/>, <see cref="Any"/> or <see cref="Not"/>; <see cref="Expression"/> is a condition
/// written as free text. Parts that are set must all hold.
/// </summary>
public sealed class RuleCondition
{
    public string? Field { get; set; }

    public string? Op { get; set; }

    /// <summary>Scalar or array. The text "$me" stands for the signed-in user.</summary>
    public JsonNode? Value { get; set; }

    public List<RuleCondition>? All { get; set; }

    public List<RuleCondition>? Any { get; set; }

    public RuleCondition? Not { get; set; }

    /// <summary>A custom expression, e.g. <c>priorita = "Alta" o coda = "Urgenze"</c> (see <see cref="RuleExpression"/>).</summary>
    public string? Expression { get; set; }
}

/// <param name="MeId">Dataverse user id of the signed-in user (from WhoAmI), for "$me".</param>
public sealed record EvaluationContext(DateTimeOffset Now, TicketMapping Mapping, Guid? MeId = null);

public sealed record RuleMatch(Rule Rule, TicketEvent Event);
