using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Rules;
using Xunit;

namespace LiveHelpDesk.Tests;

public class RuleEngineTests
{
    private static readonly Guid Me = T.Id(900);
    private static readonly EvaluationContext Context = new(T.At(120), TicketMapping.Qgs, Me);

    private static Rule Load(string json)
    {
        var result = RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{json}} ] }""");
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.File!.Rules[0];
    }

    private static Rule WithCondition(string condition, string events = "\"TicketCreated\"") =>
        Load($$"""{ "id": "r", "events": [ {{events}} ], "when": {{condition}}, "actions": { "channels": ["toast"] } }""");

    private static TicketEvent Created(TicketSnapshot ticket) =>
        new(DedupKeys.Created(ticket.Id), TicketEventType.TicketCreated, ticket.CreatedOn, ticket.Id, ticket.Number, ticket.Title, Ticket: ticket);

    private static bool Hits(Rule rule, TicketEvent evt) => RuleEngine.Evaluate(evt, [rule], Context).Count == 1;

    [Fact]
    public void ARuleWithoutACondition_MatchesItsEvents_AndOnlyThose()
    {
        var rule = Load("""{ "id": "r", "events": ["TicketCreated"], "actions": { "channels": ["toast"] } }""");
        var ticket = T.Ticket();

        Assert.True(Hits(rule, Created(ticket)));
        Assert.False(Hits(rule, Created(ticket) with { Type = TicketEventType.StatusChanged }));
    }

    [Fact]
    public void ADisabledRule_NeverMatches()
    {
        var rule = Load("""{ "id": "r", "enabled": false, "events": ["TicketCreated"], "actions": { "channels": ["toast"] } }""");

        Assert.False(Hits(rule, Created(T.Ticket())));
    }

    [Theory]
    [InlineData("1", 1, true)]
    [InlineData("\"1\"", 1, true)]
    [InlineData("1", 2, false)]
    public void Equality_ComparesNumbersAsNumbers_WhateverTheJsonType(string json, int priority, bool expected)
    {
        var rule = WithCondition($$"""{ "field": "priority", "op": "eq", "value": {{json}} }""");

        Assert.Equal(expected, Hits(rule, Created(T.Ticket(priority: priority))));
    }

    [Fact]
    public void In_AndNotIn_UseLists()
    {
        var inRule = WithCondition("""{ "field": "status", "op": "in", "value": [717600002, 717600003] }""");
        var notInRule = WithCondition("""{ "field": "status", "op": "notIn", "value": [717600002] }""");

        Assert.True(Hits(inRule, Created(T.Ticket(status: 717600003))));
        Assert.False(Hits(inRule, Created(T.Ticket(status: 2))));
        Assert.True(Hits(notInRule, Created(T.Ticket(status: 2))));
        Assert.False(Hits(notInRule, Created(T.Ticket(status: 717600002))));
    }

    [Fact]
    public void DollarMe_StandsForTheSignedInUser()
    {
        var mine = WithCondition("""{ "field": "owner", "op": "eq", "value": "$me" }""");

        Assert.True(Hits(mine, Created(T.Ticket(owner: Me))));
        Assert.False(Hits(mine, Created(T.Ticket(owner: T.Id(5)))));
        Assert.False(Hits(mine, Created(T.Ticket(owner: null))));
    }

    [Fact]
    public void NullChecks_FindUnassignedTickets()
    {
        var unassigned = WithCondition("""{ "field": "owner", "op": "isNull" }""");

        Assert.True(Hits(unassigned, Created(T.Ticket(owner: null))));
        Assert.False(Hits(unassigned, Created(T.Ticket(owner: Me))));
    }

    [Fact]
    public void TextOperators_IgnoreCase()
    {
        var contains = WithCondition("""{ "field": "title", "op": "contains", "value": "FATTURA" }""");
        var starts = WithCondition("""{ "field": "title", "op": "startsWith", "value": "richiesta" }""");
        var regex = WithCondition("""{ "field": "title", "op": "regex", "value": "ticket°\\d{4}" }""");

        Assert.True(Hits(contains, Created(T.Ticket(title: "Data fattura errata"))));
        Assert.True(Hits(starts, Created(T.Ticket(title: "Richiesta supporto"))));
        Assert.True(Hits(regex, Created(T.Ticket(title: "Supporto ticket°2026"))));
        Assert.False(Hits(regex, Created(T.Ticket(title: "Supporto ticket"))));
    }

    [Fact]
    public void Age_ComparesInMinutesAgainstTheContextClock()
    {
        var older = WithCondition("""{ "field": "ageMinutes", "op": "gt", "value": 60 }""");

        Assert.True(Hits(older, Created(T.Ticket(created: T.At(0)))));
        Assert.False(Hits(older, Created(T.Ticket(created: T.At(100)))));
    }

    [Fact]
    public void ExtraFields_AreAvailableByName()
    {
        var rule = WithCondition("""{ "field": "extra.applicativo", "op": "eq", "value": "BFA" }""");
        var extra = new Dictionary<string, string?> { ["applicativo"] = "BFA" };

        Assert.True(Hits(rule, Created(T.Ticket(extra: extra))));
        Assert.False(Hits(rule, Created(T.Ticket())));
    }

    [Fact]
    public void LevelField_UsesTheMapping()
    {
        var l2 = WithCondition("""{ "field": "level", "op": "eq", "value": 2 }""");

        Assert.True(Hits(l2, Created(T.Ticket(status: 2))));
        Assert.False(Hits(l2, Created(T.Ticket(status: 717600003))));
    }

    [Fact]
    public void Groups_CombineWithAllAnyAndNot()
    {
        var rule = WithCondition(
            """
            { "all": [
                { "field": "priority", "op": "eq", "value": 1 },
                { "any": [
                    { "field": "title", "op": "contains", "value": "errore" },
                    { "field": "title", "op": "contains", "value": "bloccante" } ] },
                { "not": { "field": "onHold", "op": "eq", "value": true } } ] }
            """);

        Assert.True(Hits(rule, Created(T.Ticket(priority: 1, title: "Errore impegno"))));
        Assert.True(Hits(rule, Created(T.Ticket(priority: 1, title: "Problema bloccante"))));
        Assert.False(Hits(rule, Created(T.Ticket(priority: 2, title: "Errore impegno"))));
        Assert.False(Hits(rule, Created(T.Ticket(priority: 1, title: "Domanda"))));
        Assert.False(Hits(rule, Created(T.Ticket(priority: 1, title: "Errore", onHold: true))));
    }

    [Fact]
    public void AMissingField_FailsEqualityButPassesInequality()
    {
        var evt = new TicketEvent("k", TicketEventType.MonitorHealth, T.At(0));
        var eq = Load("""{ "id": "a", "events": ["MonitorHealth"], "when": { "field": "owner", "op": "eq", "value": "x" }, "actions": { "channels": ["toast"] } }""");
        var ne = Load("""{ "id": "b", "events": ["MonitorHealth"], "when": { "field": "owner", "op": "ne", "value": "x" }, "actions": { "channels": ["toast"] } }""");

        Assert.False(Hits(eq, evt));
        Assert.True(Hits(ne, evt));
    }

    [Fact]
    public void MessageFields_FilterOnTheSender()
    {
        var rule = Load(
            """
            { "id": "r", "events": ["MessageReceived"], "actions": { "channels": ["toast"] },
              "when": { "field": "message.from", "op": "contains", "value": "@cliente.it" } }
            """);
        var evt = new TicketEvent("k", TicketEventType.MessageReceived, T.At(0), Ticket: T.Ticket(),
            Message: new MessageSnapshot(T.Id(1), T.Id(1), MessageKind.Email, true, T.At(0), "mario@cliente.it"));

        Assert.True(Hits(rule, evt));
        Assert.False(Hits(rule, evt with { Message = evt.Message! with { From = "altri@example.com" } }));
    }

    [Fact]
    public void DryRun_CountsMatchesPerRule_IncludingRulesThatMatchNothing()
    {
        var urgent = Load("""{ "id": "urgent", "events": ["TicketCreated"], "when": { "field": "priority", "op": "eq", "value": 1 }, "actions": { "channels": ["toast"] } }""");
        var never = Load("""{ "id": "never", "events": ["TicketCreated"], "when": { "field": "priority", "op": "eq", "value": 9 }, "actions": { "channels": ["toast"] } }""");
        var tickets = new[] { T.Ticket(1, priority: 1), T.Ticket(2, priority: 2), T.Ticket(3, priority: 1) };

        var report = RuleEngine.DryRun([urgent, never], RuleEngine.AsNewTicketEvents(tickets), Context);

        Assert.Equal(3, report.EventsEvaluated);
        Assert.Equal(1, report.EventsWithoutMatch);
        Assert.Equal(2, report.MatchesByRule["urgent"]);
        Assert.Equal(0, report.MatchesByRule["never"]);
        Assert.Equal(2, report.Matches.Count);
    }
}
