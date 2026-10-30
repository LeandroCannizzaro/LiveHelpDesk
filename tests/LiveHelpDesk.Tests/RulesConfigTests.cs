using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Rules;
using Xunit;

namespace LiveHelpDesk.Tests;

public class RulesConfigTests
{
    private const string GoodRule =
        """{ "id": "a", "name": "Nuovi ticket urgenti", "events": ["TicketCreated"], "actions": { "channels": ["toast", "overlay"], "severity": "Critical" } }""";

    [Fact]
    public void ACurrentFile_ParsesWithEnumsAsText()
    {
        var result = RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{GoodRule}} ] }""");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var rule = Assert.Single(result.File!.Rules);
        Assert.Equal([TicketEventType.TicketCreated], rule.Events);
        Assert.Equal(Severity.Critical, rule.Actions.Severity);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void ALegacyBareList_IsMigratedToTheCurrentVersion()
    {
        var result = RulesConfig.Parse($"[ {GoodRule} ]");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(RulesConfig.CurrentVersion, result.File!.Version);
        Assert.Single(result.File.Rules);
    }

    [Fact]
    public void AnObjectWithoutAVersion_IsMigrated()
    {
        var result = RulesConfig.Parse($$"""{ "rules": [ {{GoodRule}} ] }""");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(RulesConfig.CurrentVersion, result.File!.Version);
    }

    [Fact]
    public void AFileFromTheFuture_IsRejectedInsteadOfBeingMisread()
    {
        var result = RulesConfig.Parse("""{ "version": 99, "rules": [] }""");

        Assert.False(result.IsValid);
        Assert.Null(result.File);
        Assert.Contains("99", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ not json")]
    public void UnreadableText_IsReportedNotThrown(string text)
    {
        var result = RulesConfig.Parse(text);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void RoundTrip_PreservesTheRules()
    {
        var original = RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{GoodRule}} ] }""").File!;

        var again = RulesConfig.Parse(RulesConfig.Serialize(original));

        Assert.True(again.IsValid, string.Join("; ", again.Errors));
        Assert.Equal("Nuovi ticket urgenti", again.File!.Rules[0].Name);
        Assert.Equal(["toast", "overlay"], again.File.Rules[0].Actions.Channels);
    }

    [Theory]
    [InlineData("""{ "id": "a", "events": [], "actions": { "channels": ["toast"] } }""", "nessun evento")]
    [InlineData("""{ "id": "a", "events": ["TicketCreated"], "actions": { "channels": [] } }""", "nessun canale")]
    [InlineData("""{ "id": "", "events": ["TicketCreated"], "actions": { "channels": ["toast"] } }""", "manca l'id")]
    [InlineData("""{ "id": "a", "events": ["TicketCreated"], "actions": { "channels": ["toast"], "escalateAfterMinutes": 10 } }""", "escalation senza canali")]
    [InlineData("""{ "id": "a", "events": ["TicketCreated"], "actions": { "channels": ["toast"], "escalateAfterMinutes": 0, "escalateChannels": ["sms"] } }""", "maggiori di zero")]
    public void RuleLevelMistakes_AreExplained(string rule, string expected)
    {
        var result = RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{rule}} ] }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expected));
    }

    [Theory]
    [InlineData("""{ "field": "colore", "op": "eq", "value": "x" }""", "campo sconosciuto")]
    [InlineData("""{ "field": "priority", "op": "uguale", "value": 1 }""", "operatore sconosciuto")]
    [InlineData("""{ "field": "priority", "op": "eq" }""", "richiede un valore")]
    [InlineData("""{ "field": "priority", "op": "in", "value": 1 }""", "elenco di valori")]
    [InlineData("""{ "field": "title", "op": "regex", "value": "(" }""", "espressione regolare")]
    [InlineData("""{ "all": [] }""", "è vuoto")]
    [InlineData("""{ }""", "condizione vuota")]
    public void ConditionMistakes_AreExplained(string condition, string expected)
    {
        var rule = $$"""{ "id": "a", "events": ["TicketCreated"], "actions": { "channels": ["toast"] }, "when": {{condition}} }""";

        var result = RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{rule}} ] }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        var result = RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{GoodRule}}, {{GoodRule}} ] }""");

        Assert.Contains(result.Errors, e => e.Contains("duplicato"));
    }

    [Fact]
    public void ExtraFields_AreValidWithAnyName()
    {
        var rule = $$"""{ "id": "a", "events": ["TicketCreated"], "actions": { "channels": ["toast"] }, "when": { "field": "extra.cliente", "op": "eq", "value": "x" } }""";

        Assert.True(RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{rule}} ] }""").IsValid);
    }
}

public class ChannelPolicyTests
{
    private static DateTimeOffset Local(int day, int hour) => new(2026, 11, day, hour, 0, 0, TimeSpan.FromHours(1)); // 2026-11-02 is a Monday

    private static ChannelPolicy Policy(Severity? bypass = null) => new()
    {
        Channels =
        {
            ["sms"] = new ChannelSchedule
            {
                AlwaysAllowFrom = bypass,
                Windows = [new ChannelWindow { From = new TimeOnly(20, 0), To = new TimeOnly(8, 0) }],
            },
        },
    };

    [Fact]
    public void AChannelWithoutASchedule_IsAlwaysAllowed() =>
        Assert.True(Policy().Allows("email", Severity.Info, Local(2, 12)));

    [Theory]
    [InlineData(21, true)]
    [InlineData(23, true)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(12, false)]
    [InlineData(19, false)]
    public void AWindowCrossingMidnight_CoversEveningAndMorning(int hour, bool expected) =>
        Assert.Equal(expected, Policy().Allows("sms", Severity.Info, Local(2, hour)));

    [Fact]
    public void ACriticalEvent_BypassesTheWindows()
    {
        var policy = Policy(bypass: Severity.Critical);

        Assert.False(policy.Allows("sms", Severity.Attention, Local(2, 12)));
        Assert.True(policy.Allows("sms", Severity.Critical, Local(2, 12)));
    }

    [Fact]
    public void ADaytimeWindow_OnlyOnTheListedDays()
    {
        var policy = new ChannelPolicy
        {
            Channels =
            {
                ["email"] = new ChannelSchedule
                {
                    Windows = [new ChannelWindow { Days = [DayOfWeek.Monday, DayOfWeek.Tuesday], From = new TimeOnly(9, 0), To = new TimeOnly(18, 0) }],
                },
            },
        };

        Assert.True(policy.Allows("email", Severity.Info, Local(2, 10))); // Monday
        Assert.False(policy.Allows("email", Severity.Info, Local(2, 19)));
        Assert.False(policy.Allows("email", Severity.Info, Local(4, 10))); // Wednesday
    }

    [Fact]
    public void ChannelNames_AreMatchedIgnoringCase() =>
        Assert.False(Policy().Allows("SMS", Severity.Info, Local(2, 12)));
}
