using System.Text.Json.Nodes;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Ipc;
using LiveHelpDesk.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveHelpDesk.Tests;

public class RuleEditorTests
{
    private static readonly IReadOnlyList<LookupItem> Lookups =
    [
        new(FakeApi.QueueId.ToString("D"), "Assistenza L1", "queue"),
        new("aaaaaaaa-0000-0000-0000-000000000001", "Mario Rossi", "user"),
    ];

    private static IReadOnlyList<FieldInfo> Fields() => RuleCatalog.Fields(TicketMapping.Qgs, Lookups);

    private static FieldInfo Field(string key) => Fields().First(f => f.Key == key);

    private static string Json(Rule rule) => RulesConfig.Serialize(new RulesFile { Rules = [rule] });

    private static Rule Parse(string rule) => RulesConfig.Parse($$"""{ "version": 1, "rules": [ {{rule}} ] }""").File!.Rules[0];

    [Fact]
    public void TheStartingRules_SurviveBeingOpenedAndSavedAgain_Unchanged()
    {
        foreach (var rule in DefaultRules.Create().Rules)
        {
            var again = new RuleViewModel(rule, Fields()).ToRule();

            Assert.Equal(Json(rule), Json(again));
        }
    }

    [Fact]
    public void AllAnyAndNotConditions_AreDrawnInTheirThreeLists_AndWrittenBackIdentically()
    {
        var rule = Parse(
            """
            { "id": "r", "name": "Urgenti", "events": ["TicketCreated", "CustomerReplied"],
              "actions": { "channels": ["toast", "sms"], "severity": "Critical", "detail": "Minimal", "escalateAfterMinutes": 20, "escalateChannels": ["email"] },
              "when": { "all": [ { "field": "priority", "op": "eq", "value": 1 }, { "field": "status", "op": "in", "value": [717600002, 717600003] } ],
                        "any": [ { "field": "title", "op": "contains", "value": "errore" }, { "field": "title", "op": "contains", "value": "bloccante" } ],
                        "not": { "field": "onHold", "op": "eq", "value": true } } }
            """);

        var model = new RuleViewModel(rule, Fields());

        Assert.False(model.IsAdvanced);
        Assert.Equal(2, model.AllRows.Count);
        Assert.Equal(2, model.AnyRows.Count);
        Assert.Single(model.NotRows);
        Assert.Equal("Alta", model.AllRows[0].ValueChoice!.Label);
        Assert.Equal("Critico", model.Severity.Label);
        Assert.True(model.EscalateEnabled);
        Assert.Equal(Json(rule), Json(model.ToRule()));
    }

    [Fact]
    public void ConditionsTooElaborateForTheForm_AreKeptUntouched_WhileTheRestStaysEditable()
    {
        var rule = Parse(
            """
            { "id": "r", "name": "Complessa", "events": ["TicketCreated"], "actions": { "channels": ["toast"] },
              "when": { "any": [ { "all": [ { "field": "priority", "op": "eq", "value": 1 }, { "field": "title", "op": "contains", "value": "x" } ] },
                                 { "field": "owner", "op": "eq", "value": "$me" } ] } }
            """);

        var model = new RuleViewModel(rule, Fields());
        model.Name = "Complessa (rinominata)";
        model.ChannelChoices.First(c => c.Value == "overlay").IsSelected = true;
        var saved = model.ToRule();

        Assert.True(model.IsAdvanced);
        Assert.Equal("Complessa (rinominata)", saved.Name);
        Assert.Equal(["toast", "overlay"], saved.Actions.Channels);
        Assert.Same(rule.When, saved.When);
    }

    [Fact]
    public void ARowBuiltInTheForm_WritesTheValuesTheFileExpects()
    {
        var model = new RuleViewModel(new Rule { Name = "n" }, Fields());

        var priority = new ConditionRow(model.Fields, _ => { }, Field("priority"));
        priority.ValueChoice = priority.Choices.First(c => c.Label == "Alta");
        var onHold = new ConditionRow(model.Fields, _ => { }, Field("onHold"));
        onHold.ValueChoice = onHold.Choices.First(c => c.Label == "No");
        var several = new ConditionRow(model.Fields, _ => { }, Field("priority"));
        several.Operator = several.Operators.First(o => o.Value == "in");
        several.Value = "1, 2";
        var age = new ConditionRow(model.Fields, _ => { }, Field("ageMinutes"));
        age.Value = "60";
        var unassigned = new ConditionRow(model.Fields, _ => { }, Field("owner"));
        unassigned.Operator = unassigned.Operators.First(o => o.Value == "isNull");
        var mine = new ConditionRow(model.Fields, _ => { }, Field("owner"));
        mine.ValueChoice = mine.Choices.First(c => c.Label == "Io");
        var text = new ConditionRow(model.Fields, _ => { }, Field("title"));
        text.Value = "fattura";

        Assert.Equal(1L, (long)priority.ToCondition().Value!);
        Assert.Equal(false, (bool)onHold.ToCondition().Value!);
        Assert.Equal([1L, 2L], ((JsonArray)several.ToCondition().Value!).Select(v => (long)v!).ToArray());
        Assert.Equal("gt", age.ToCondition().Op);
        Assert.Equal(60L, (long)age.ToCondition().Value!);
        Assert.Null(unassigned.ToCondition().Value);
        Assert.Equal("isNull", unassigned.ToCondition().Op);
        Assert.Equal("$me", (string)mine.ToCondition().Value!);
        Assert.Equal("contains", text.ToCondition().Op);
        Assert.Equal("fattura", (string)text.ToCondition().Value!);
    }

    [Fact]
    public void ChangingTheField_OffersItsOwnOperators_AndClearsTheOldValue()
    {
        var row = new ConditionRow(Fields(), _ => { }, Field("title")) { Value = "fattura" };
        Assert.Equal("contains", row.Operator!.Value);

        row.Field = Field("ageMinutes");

        Assert.Equal("gt", row.Operator!.Value);
        Assert.Equal("", row.Value);
        Assert.DoesNotContain(row.Operators, o => o.Value == "contains");
    }

    [Fact]
    public void QueuesAndUsers_AreOfferedByName_AndAnUnknownIdIsStillKept()
    {
        var queue = Field("queue");
        Assert.Equal(["Assistenza L1"], queue.Choices.Select(c => c.Label).ToArray());
        Assert.Contains("Io", Field("owner").Choices.Select(c => c.Label));
        Assert.Contains("Mario Rossi", Field("owner").Choices.Select(c => c.Label));

        var rule = Parse("""{ "id": "r", "events": ["TicketCreated"], "actions": { "channels": ["toast"] }, "when": { "field": "queue", "op": "eq", "value": "dddddddd-0000-0000-0000-00000000dead" } }""");
        var model = new RuleViewModel(rule, Fields());

        Assert.Equal("dddddddd-0000-0000-0000-00000000dead", model.AllRows[0].ValueChoice!.Value);
        Assert.Equal(Json(rule), Json(model.ToRule()));
    }

    [Fact]
    public void AFieldTheFormDoesNotKnow_IsKeptWorking()
    {
        var rule = Parse("""{ "id": "r", "events": ["TicketCreated"], "actions": { "channels": ["toast"] }, "when": { "field": "commStatus", "op": "eq", "value": 717600002 } }""");

        var model = new RuleViewModel(rule, Fields());

        Assert.Equal("Campo: commStatus", model.AllRows[0].Field.Label);
        Assert.Equal(Json(rule), Json(model.ToRule()));
    }

    [Fact]
    public void ANewRule_GetsAnIdFromItsName_NeverClashingWithAnother()
    {
        var model = new RuleViewModel(new Rule { Name = "Nuovi ticket urgenti!" }, Fields());

        model.EnsureId(["nuovi-ticket-urgenti", "altra"]);

        Assert.Equal("nuovi-ticket-urgenti-2", model.Id);
        model.EnsureId([]);
        Assert.Equal("nuovi-ticket-urgenti-2", model.Id); // an id never changes once given
    }

    [Fact]
    public void AnyChange_IsReported_ToTheEditorsDirtyFlag()
    {
        var model = new RuleViewModel(DefaultRules.Create().Rules[0], Fields());
        var edits = 0;
        model.Edited += (_, _) => edits++;

        model.Name = "altro";
        model.ChannelChoices[0].IsSelected = !model.ChannelChoices[0].IsSelected;
        model.AddAllCommand.Execute(null);
        model.AllRows[0].Value = "x";

        Assert.True(edits >= 4);
    }
}

public class RulesViewModelTests
{
    [Fact]
    public async Task OpeningTheEditor_ShowsTheServicesRules_WithNothingMarkedAsChanged()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);

        await vm.LoadAsync();

        Assert.Equal(4, vm.Rules.Count);
        Assert.Equal("Nuovi ticket", vm.Selected!.Name);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task AddingARule_MarksTheEditorDirty_AndSavingSendsItWithAnIdAndClearsTheFlag()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();

        vm.AddRuleCommand.Execute(null);
        vm.Selected!.Name = "Messaggi di BFA";
        Assert.True(vm.IsDirty);
        await vm.SaveCommand.ExecuteAsync(null);

        var saved = RulesConfig.Parse(api.SavedRules!).File!.Rules;
        Assert.Equal(5, saved.Count);
        Assert.Equal("messaggi-di-bfa", saved[^1].Id);
        Assert.False(vm.IsDirty);
        Assert.Contains("già attive", vm.Status);
    }

    [Fact]
    public async Task ARuleWithNothingToTriggerOn_IsNotSent_AndTheMistakeIsShown()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();
        foreach (var e in vm.Selected!.EventChoices)
        {
            e.IsSelected = false;
        }

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(api.SavedRules);
        Assert.Contains(vm.Errors, e => e.Contains("nessun evento"));
        Assert.True(vm.HasErrors);
        Assert.Contains("Non salvato", vm.Status);
    }

    [Fact]
    public async Task ThePreview_UsesTheUnsavedRules_AndSaysHowManyTicketsMatch()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();
        vm.Selected!.Name = "Rinominata ma non salvata";

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Contains("Rinominata ma non salvata", api.DraftChecked);
        Assert.Null(api.SavedRules);
        Assert.Equal("2 ticket", vm.Preview[0].Matches);
        Assert.Equal("nessun ticket", vm.Preview[1].Matches);
        Assert.Contains("#261019389 data fattura", vm.Preview[0].Examples);
        Assert.True(vm.HasPreview);
        Assert.Contains("446 ticket attivi", vm.Status);
    }

    [Fact]
    public async Task TheTextView_CanBeEdited_AndAppliedBackToTheForm()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();

        vm.ToggleJsonCommand.Execute(null);
        Assert.True(vm.ShowJson);
        Assert.Contains("\"new-tickets\"", vm.JsonText);

        vm.JsonText = """{ "version": 1, "rules": [ { "id": "solo", "name": "Solo una", "events": ["TicketCreated"], "actions": { "channels": ["toast"] } } ] }""";
        vm.ApplyJsonCommand.Execute(null);

        Assert.False(vm.ShowJson);
        Assert.Equal(["Solo una"], vm.Rules.Select(r => r.Name).ToArray());
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task TextThatIsNotValid_StaysInTheTextView_WithItsErrors()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();
        vm.ToggleJsonCommand.Execute(null);

        vm.JsonText = "{ non è json";
        vm.ApplyJsonCommand.Execute(null);

        Assert.True(vm.ShowJson);
        Assert.True(vm.HasErrors);
        Assert.Equal(4, vm.Rules.Count); // the form still has the last good rules
    }

    [Fact]
    public async Task AFileThatCannotBeRead_IsShownAsText_SoItCanBeMended()
    {
        var api = new FakeApi { RulesJson = "{ rotto" };
        var vm = new RulesViewModel(() => api);

        await vm.LoadAsync();

        Assert.True(vm.ShowJson);
        Assert.Equal("{ rotto", vm.JsonText);
        Assert.True(vm.HasErrors);
        Assert.Empty(vm.Rules);
    }

    [Fact]
    public async Task TheServiceRefusingTheRules_IsReportedWithItsReasons()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();
        var refusing = new RefusingApi(api);
        var vm2 = new RulesViewModel(() => refusing);
        await vm2.LoadAsync();

        await vm2.SaveCommand.ExecuteAsync(null);

        Assert.Contains("il servizio ha trovato degli errori", vm2.Status);
        Assert.Contains("Un motivo del servizio", vm2.Errors);
    }

    [Fact]
    public async Task Duplicating_RemovingAndRestoringDefaults_AllMarkTheEditorDirty()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();

        vm.DuplicateRuleCommand.Execute(null);
        Assert.Equal(5, vm.Rules.Count);
        Assert.Equal("Nuovi ticket (copia)", vm.Selected!.Name);
        Assert.True(vm.IsDirty);

        vm.RemoveRuleCommand.Execute(null);
        Assert.Equal(4, vm.Rules.Count);

        vm.Rules.Clear();
        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Equal(4, vm.Rules.Count);
        Assert.True(vm.IsDirty);

        await vm.DiscardCommand.ExecuteAsync(null);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task WithoutTheService_NothingIsSavedAndTheUserIsTold()
    {
        var vm = new RulesViewModel(() => null);

        await vm.SaveCommand.ExecuteAsync(null);
        await vm.LoadAsync();

        Assert.Contains("non è raggiungibile", vm.Status);
    }

    /// <summary>An API that refuses every save, the way the service does for a file it finds wrong.</summary>
    private sealed class RefusingApi(FakeApi inner) : ILiveHelpDeskApi
    {
        public event EventHandler<ChangedEventArgs>? Changed { add { } remove { } }

        public Task<RulesSaveResult> SaveRulesJsonAsync(string json) => Task.FromResult(new RulesSaveResult(false, ["Un motivo del servizio"]));

        public Task<string> GetRulesJsonAsync() => inner.GetRulesJsonAsync();

        public Task<IReadOnlyList<LookupItem>> GetLookupsAsync() => inner.GetLookupsAsync();

        public Task<ServiceStatus> GetStatusAsync() => inner.GetStatusAsync();

        public Task<LoginChallenge> StartLoginAsync() => inner.StartLoginAsync();

        public Task<IReadOnlyList<EventInfo>> GetRecentEventsAsync(int count) => inner.GetRecentEventsAsync(count);

        public Task AcknowledgeAsync(string eventKey) => inner.AcknowledgeAsync(eventKey);

        public Task<IReadOnlyList<NotificationInfo>> ClaimNotificationsAsync(IReadOnlyList<string> channels) => inner.ClaimNotificationsAsync(channels);

        public Task<DryRunResult> DryRunRulesAsync() => inner.DryRunRulesAsync();

        public Task<DryRunResult> DryRunDraftAsync(string rulesJson) => inner.DryRunDraftAsync(rulesJson);

        public Task<ChannelSettings> GetChannelSettingsAsync() => inner.GetChannelSettingsAsync();

        public Task SaveChannelSettingsAsync(ChannelSettings settings) => inner.SaveChannelSettingsAsync(settings);

        public Task<IReadOnlyList<ChannelInfo>> GetChannelStatusAsync() => inner.GetChannelStatusAsync();

        public Task<ChannelTestResult> TestChannelAsync(string channelId) => inner.TestChannelAsync(channelId);

        public Task<LoginChallenge> StartMailLoginAsync() => inner.StartMailLoginAsync();

        public Task<MailAccountInfo> GetMailAccountAsync() => inner.GetMailAccountAsync();

        public Task SignOutMailAsync() => inner.SignOutMailAsync();
    }
}

public class QuietHoursEditorTests
{
    private static QuietHoursEditor Editor(ChannelSchedule? schedule = null, int limit = 10) => new(schedule, limit);

    [Fact]
    public void ANightTimeQuietPeriod_BecomesOneAllowedWindowDuringTheDay()
    {
        var editor = Editor();
        editor.Enabled = true;
        editor.From = "22:00";
        editor.To = "07:00";
        editor.CriticalPasses = true;

        var schedule = editor.ToSchedule(out var error)!;

        Assert.Null(error);
        var window = Assert.Single(schedule.Windows);
        Assert.Equal(new TimeOnly(7, 0), window.From);
        Assert.Equal(new TimeOnly(22, 0), window.To);
        Assert.Equal(Severity.Critical, schedule.AlwaysAllowFrom);
    }

    [Fact]
    public void ADaytimeQuietPeriod_BecomesTwoAllowedWindowsAroundIt()
    {
        var editor = Editor();
        editor.Enabled = true;
        editor.From = "12:00";
        editor.To = "14:00";
        editor.CriticalPasses = false;

        var schedule = editor.ToSchedule(out _)!;

        Assert.Equal(2, schedule.Windows.Count);
        Assert.Equal((TimeOnly.MinValue, new TimeOnly(12, 0)), (schedule.Windows[0].From, schedule.Windows[0].To));
        Assert.Equal((new TimeOnly(14, 0), TimeOnly.MaxValue), (schedule.Windows[1].From, schedule.Windows[1].To));
        Assert.Null(schedule.AlwaysAllowFrom);
    }

    [Theory]
    [InlineData("22:00", "07:00")]
    [InlineData("12:00", "14:00")]
    public void WhatTheEditorWrites_ItReadsBackAsTheSameQuietPeriod(string from, string to)
    {
        var editor = Editor();
        editor.Enabled = true;
        editor.From = from;
        editor.To = to;
        editor.CriticalPasses = true;
        var schedule = editor.ToSchedule(out _);

        var reopened = Editor(schedule);

        Assert.True(reopened.Enabled);
        Assert.False(reopened.IsCustom);
        Assert.Equal((from, to), (reopened.From, reopened.To));
        Assert.True(reopened.CriticalPasses);
    }

    [Theory]
    [InlineData("22:00", "22:00")]
    [InlineData("tardi", "07:00")]
    [InlineData("22:00", "25:00")]
    public void NonsenseTimes_AreRefusedWithAnExplanation(string from, string to)
    {
        var editor = Editor();
        editor.Enabled = true;
        editor.From = from;
        editor.To = to;

        var schedule = editor.ToSchedule(out var error);

        Assert.Null(schedule);
        Assert.Contains("Orari non validi", error);
    }

    [Fact]
    public void AScheduleWrittenByHand_IsLeftAloneByTheForm()
    {
        var custom = new ChannelSchedule { Windows = [new ChannelWindow { Days = [DayOfWeek.Monday], From = new TimeOnly(9, 0), To = new TimeOnly(18, 0) }] };

        var editor = Editor(custom);

        Assert.True(editor.IsCustom);
        Assert.Same(custom, editor.ToSchedule(out _));
    }

    [Fact]
    public void NoQuietPeriod_MeansNoSchedule_AndTheLimitIsKept()
    {
        var editor = Editor(null, 25);

        Assert.Null(editor.ToSchedule(out _));
        Assert.Equal(25, editor.LimitValue);
        Assert.False(editor.Enabled);
    }
}

public class ChannelsViewModelTests
{
    [Fact]
    public async Task TheForm_ShowsWhatIsSaved_WithSecretsMasked()
    {
        var api = new FakeApi();
        var vm = new ChannelsViewModel(() => api);

        await vm.LoadAsync();

        Assert.True(vm.IsLoaded);
        Assert.True(vm.Email!.Enabled);
        Assert.Equal("smtp.example.com", vm.Email.Host);
        Assert.Equal("********", vm.Email.Password);
        Assert.Equal(["email", "teams", "sms", "whatsapp"], vm.All.Select(c => c.Id).ToArray());
        Assert.False(vm.IsDirty);
        Assert.True(vm.Sms!.Quiet.IsCustom); // the fake's SMS schedule has a day list the form cannot draw
    }

    [Fact]
    public async Task EditingAField_IsSentOnSave_WithTheMaskedSecretLeftAlone()
    {
        var api = new FakeApi();
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();

        vm.Email!.Host = "smtp2.example.com";
        vm.Email.To = "a@example.com, b@example.com\nc@example.com";
        Assert.True(vm.IsDirty);
        await vm.SaveCommand.ExecuteAsync(null);

        var saved = api.Saved!;
        Assert.Equal("smtp2.example.com", saved.Email.Host);
        Assert.Equal(["a@example.com", "b@example.com", "c@example.com"], saved.Email.To);
        Assert.Equal("********", saved.Email.Password); // untouched: the service keeps the real one
        Assert.False(vm.IsDirty);
        Assert.Contains("Salvato", vm.Status);
    }

    [Fact]
    public async Task QuietHoursAndLimits_AreSavedPerChannel()
    {
        var api = new FakeApi();
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();

        vm.Email!.Quiet.Enabled = true;
        vm.Email.Quiet.From = "21:00";
        vm.Email.Quiet.To = "08:00";
        vm.Email.Quiet.Limit = "12";
        await vm.SaveCommand.ExecuteAsync(null);

        var saved = api.Saved!;
        Assert.Equal(new TimeOnly(8, 0), saved.Schedules["email"].Windows[0].From);
        Assert.Equal(12, saved.MaxPerHour["email"]);
        Assert.Same(api.Settings.Schedules["sms"].Windows[0].Days.Count > 0 ? "x" : "x", "x");
        Assert.Equal([DayOfWeek.Monday], saved.Schedules["sms"].Windows[0].Days.ToArray()); // the hand-written one is intact
    }

    [Fact]
    public async Task BadQuietHours_AreNotSent()
    {
        var api = new FakeApi();
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();
        vm.Email!.Quiet.Enabled = true;
        vm.Email.Quiet.From = "xx";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(api.Saved);
        Assert.Contains("Orari non validi", vm.Status);
    }

    [Fact]
    public async Task TheTestButton_AsksToSaveFirst_WhenThereAreUnsavedChanges()
    {
        var api = new FakeApi();
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();
        vm.Email!.Host = "altro";

        await vm.Email.TestCommand.ExecuteAsync(null);

        Assert.Contains("Salva prima di provare", vm.Email.TestText);
        Assert.Empty(api.Tested);
    }

    [Fact]
    public async Task TheTestButton_SendsATest_AndReportsTheOutcome()
    {
        var api = new FakeApi();
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();

        await vm.Email!.TestCommand.ExecuteAsync(null);
        await vm.Sms!.TestCommand.ExecuteAsync(null);

        Assert.Equal(["email", "sms"], api.Tested);
        Assert.Equal("Messaggio di prova inviato.", vm.Email.TestText);
        Assert.Contains("canale non attivo", vm.Sms.TestText);
    }

    [Fact]
    public async Task WithoutTheService_TheFormSaysSo()
    {
        var vm = new ChannelsViewModel(() => null);

        await vm.LoadAsync();
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsLoaded);
        Assert.Contains("non è raggiungibile", vm.Status);
    }
}

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _runKey = $@"Software\LiveHelpDeskTests\{Guid.NewGuid():N}\Run";
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"lhd-ui-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_runKey.Split('\\')[0] + @"\" + _runKey.Split('\\')[1], throwOnMissingSubKey: false);
        File.Delete(_file);
    }

    private (SettingsViewModel Vm, UiSettings Settings, List<string> Calls) Create(UiSettings? settings = null)
    {
        var calls = new List<string>();
        settings ??= new UiSettings();
        var vm = new SettingsViewModel(
            settings, new AutoStart(_runKey, "LiveHelpDeskTest"), @"C:\Programmi\LiveHelpDesk\LiveHelpDesk.exe",
            ["Schermo 1 (principale) · 1920×1080", "Schermo 2 · 1366×768"], () => calls.Add("demo"), () => calls.Add("changed"));
        return (vm, settings, calls);
    }

    [Fact]
    public void TheChoices_StartFromWhatIsSaved_AndApplyAtOnce()
    {
        var (vm, settings, calls) = Create(new UiSettings { OverlayEnabled = false, OverlayScreen = 2 });

        Assert.False(vm.OverlayEnabled);
        Assert.Equal("Schermo 2 · 1366×768", vm.Screen.Label);
        Assert.Equal(3, vm.Screens.Count); // "main screen" plus the two listed

        vm.OverlayEnabled = true;
        vm.PlaySound = false;
        vm.Screen = vm.Screens[1];

        Assert.True(settings.OverlayEnabled);
        Assert.False(settings.PlaySoundForCritical);
        Assert.Equal(1, settings.OverlayScreen);
        Assert.Equal(3, calls.Count(c => c == "changed"));
    }

    [Fact]
    public void ResettingThePosition_PutsTheOverlayBackAtTheTopCentre()
    {
        var (vm, settings, calls) = Create(new UiSettings { OverlayOffsetX = -300, OverlayTop = 200 });

        vm.ResetPositionCommand.Execute(null);

        Assert.Equal((0d, 8d), (settings.OverlayOffsetX, settings.OverlayTop));
        Assert.Contains("changed", calls);
    }

    [Fact]
    public void TheExampleButton_ShowsTheOverlay()
    {
        var (vm, _, calls) = Create();

        vm.ShowOverlayDemoCommand.Execute(null);

        Assert.Equal(["demo"], calls);
    }

    [Fact]
    public void StartWithWindows_WritesAndRemovesThePerUserRunEntry()
    {
        var (vm, _, _) = Create();
        var autoStart = new AutoStart(_runKey, "LiveHelpDeskTest");
        Assert.False(autoStart.IsEnabled);

        vm.StartWithWindows = true;
        Assert.True(autoStart.IsEnabled);
        Assert.Equal("\"C:\\Programmi\\LiveHelpDesk\\LiveHelpDesk.exe\" --minimized", autoStart.Command);
        Assert.Contains("avvierà da solo", vm.Note);

        vm.StartWithWindows = false;
        Assert.False(autoStart.IsEnabled);
    }

    [Fact]
    public void WhatIsAlreadyEnabledInWindows_ShowsAsChecked()
    {
        new AutoStart(_runKey, "LiveHelpDeskTest").Set(true, @"C:\x.exe");

        var (vm, _, _) = Create();

        Assert.True(vm.StartWithWindows);
    }
}

public sealed class RulesProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lhd-rules-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private RulesProvider Provider() => new(Path.Combine(_directory, "rules.json"), NullLogger.Instance);

    [Fact]
    public void AValidReplacement_TakesEffectAtOnce_AndIsWrittenNeatly()
    {
        var provider = Provider();
        Assert.Equal(4, provider.Current.Count);

        var result = provider.Replace("""{ "version": 1, "rules": [ { "id": "solo", "name": "Solo", "events": ["TicketCreated"], "actions": { "channels": ["toast"] } } ] }""");

        Assert.True(result.IsValid);
        Assert.Equal(["solo"], provider.Current.Select(r => r.Id).ToArray());
        Assert.Contains("\"id\": \"solo\"", provider.ReadJson());
    }

    [Fact]
    public void AnInvalidReplacement_ChangesNothing()
    {
        var provider = Provider();
        var before = provider.ReadJson();

        var result = provider.Replace("""{ "version": 1, "rules": [ { "id": "x", "events": [], "actions": { "channels": [] } } ] }""");

        Assert.False(result.IsValid);
        Assert.Equal(before, provider.ReadJson());
        Assert.Equal(4, provider.Current.Count);
    }

    [Fact]
    public void ARulesFileThatCannotBeRead_AtAll_IsRefusedToo()
    {
        var provider = Provider();

        Assert.False(provider.Replace("{ rotto").IsValid);
        Assert.Equal(4, provider.Current.Count);
    }
}
