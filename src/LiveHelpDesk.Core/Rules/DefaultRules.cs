using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

/// <summary>Starting rules for a fresh install; the user edits them from the app.</summary>
public static class DefaultRules
{
    public static RulesFile Create() => new()
    {
        Rules =
        [
            new Rule
            {
                Id = "new-tickets",
                Name = "Nuovi ticket",
                Events = [TicketEventType.TicketCreated],
                Actions = new RuleActions { Channels = ["toast", "overlay"], Severity = Severity.Attention },
            },
            new Rule
            {
                Id = "customer-messages",
                Name = "Messaggi e risposte dei clienti",
                Events = [TicketEventType.MessageReceived, TicketEventType.CustomerReplied],
                Actions = new RuleActions { Channels = ["toast", "overlay"], Severity = Severity.Attention },
            },
            new Rule
            {
                Id = "unattended",
                Name = "Ticket senza risposta",
                Events = [TicketEventType.Unattended],
                Actions = new RuleActions { Channels = ["toast", "overlay"], Severity = Severity.Critical },
            },
            new Rule
            {
                Id = "monitor-health",
                Name = "Problemi del monitoraggio",
                Events = [TicketEventType.MonitorHealth],
                When = new RuleCondition
                {
                    Field = "subtype",
                    Op = "in",
                    Value = new System.Text.Json.Nodes.JsonArray(
                        "AuthExpired", "AccessDenied", "DynamicsUnreachable", "PollingError", "GapDetected", "Recovered",
                        "ChannelFailing", "ChannelRecovered"),
                },
                Actions = new RuleActions { Channels = ["toast", "overlay"], Severity = Severity.Critical },
            },
        ],
    };
}
