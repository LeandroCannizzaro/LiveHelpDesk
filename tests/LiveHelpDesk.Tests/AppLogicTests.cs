using CommunityToolkit.WinUI.Notifications;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.Ipc;
using Xunit;

namespace LiveHelpDesk.Tests;

public class AttentionModelTests
{
    private static AttentionItem Item(string key, Severity severity = Severity.Attention, int minutes = 0, string? title = null) =>
        new(key, title ?? $"Titolo {key}", "corpo", severity, null, T.At(minutes));

    [Fact]
    public void Nothing_MeansNoAttention()
    {
        var model = new AttentionModel();

        Assert.Equal(AttentionLevel.None, model.Level);
        Assert.Equal(AttentionLevel.None, model.VisibleLevel(T.At(0)));
        Assert.Null(model.Headline);
    }

    [Fact]
    public void TheLevel_IsTheMostUrgentItem()
    {
        var model = new AttentionModel();
        model.Add(Item("a"), T.At(0));
        Assert.Equal(AttentionLevel.Attention, model.Level);

        model.Add(Item("b", Severity.Critical), T.At(0));
        Assert.Equal(AttentionLevel.Critical, model.Level);

        model.Acknowledge("b");
        Assert.Equal(AttentionLevel.Attention, model.Level);
    }

    [Fact]
    public void TheSameEvent_IsOnlyCountedOnce()
    {
        var model = new AttentionModel();

        Assert.True(model.Add(Item("a"), T.At(0)));
        Assert.False(model.Add(Item("a"), T.At(1)));

        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void TheHeadline_IsTheMostUrgent_ThenTheNewest()
    {
        var model = new AttentionModel();
        model.Add(Item("old", Severity.Attention, minutes: 1), T.At(5));
        model.Add(Item("new", Severity.Attention, minutes: 4), T.At(5));
        Assert.Equal("new", model.Headline!.EventKey);

        model.Add(Item("urgent", Severity.Critical, minutes: 0), T.At(5));
        Assert.Equal("urgent", model.Headline!.EventKey);
    }

    [Fact]
    public void Acknowledging_RemovesOneOrAll_AndTellsAboutIt()
    {
        var model = new AttentionModel();
        var changes = 0;
        model.Changed += (_, _) => changes++;
        model.Add(Item("a"), T.At(0));
        model.Add(Item("b"), T.At(0));

        Assert.Equal("a", model.Acknowledge("a")!.EventKey);
        Assert.Null(model.Acknowledge("a"));
        Assert.Single(model.AcknowledgeAll());
        Assert.Empty(model.AcknowledgeAll());

        Assert.Equal(AttentionLevel.None, model.Level);
        Assert.Equal(4, changes); // two adds, one acknowledge, one acknowledge-all (nothing to remove afterwards)
    }

    [Fact]
    public void Snoozing_HidesTheOverlay_ButNotTheTrayLevel()
    {
        var model = new AttentionModel();
        model.Add(Item("a"), T.At(0));

        model.Snooze(TimeSpan.FromMinutes(15), T.At(0));

        Assert.Equal(AttentionLevel.None, model.VisibleLevel(T.At(10)));
        Assert.Equal(AttentionLevel.Attention, model.Level);
        Assert.Equal(AttentionLevel.Attention, model.VisibleLevel(T.At(15)));
    }

    [Fact]
    public void ACriticalItem_BreaksThroughASnooze_AnOrdinaryOneDoesNot()
    {
        var model = new AttentionModel();
        model.Snooze(TimeSpan.FromMinutes(30), T.At(0));

        model.Add(Item("ordinary"), T.At(1));
        Assert.Equal(AttentionLevel.None, model.VisibleLevel(T.At(2)));

        model.Add(Item("urgent", Severity.Critical), T.At(3));
        Assert.Equal(AttentionLevel.Critical, model.VisibleLevel(T.At(3)));
    }

    [Fact]
    public void EndingTheSnooze_ShowsTheOverlayAgain()
    {
        var model = new AttentionModel();
        model.Add(Item("a"), T.At(0));
        model.Snooze(TimeSpan.FromHours(1), T.At(0));

        model.EndSnooze();

        Assert.Equal(AttentionLevel.Attention, model.VisibleLevel(T.At(1)));
    }

    [Fact]
    public void WhenTooManyPile_UpUrgentOnesSurvive()
    {
        var model = new AttentionModel();
        model.Add(Item("critical", Severity.Critical, minutes: 0), T.At(0));
        for (var i = 1; i <= 60; i++)
        {
            model.Add(Item($"n{i}", Severity.Info, minutes: i), T.At(i));
        }

        Assert.Equal(50, model.Count);
        Assert.Contains(model.Items, i => i.EventKey == "critical");
        Assert.DoesNotContain(model.Items, i => i.EventKey == "n1"); // the oldest of the least urgent went first
    }
}

public class NotificationPumpTests
{
    private sealed class RecordingToasts : IToastPresenter
    {
        public List<NotificationInfo> Shown { get; } = [];

        public void Show(NotificationInfo notification) => Shown.Add(notification);
    }

    private static async Task<ServiceLink> ConnectedAsync(PipeHarness harness)
    {
        var link = new ServiceLink(harness.Name, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));
        link.Start();
        for (var i = 0; i < 200 && link.State != LinkState.Connected; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(LinkState.Connected, link.State);
        return link;
    }

    [Fact]
    public async Task ToastsGoToWindows_AndOverlayItemsGoToTheAttentionModel()
    {
        await using var harness = new PipeHarness();
        await using var link = await ConnectedAsync(harness);
        var toasts = new RecordingToasts();
        var attention = new AttentionModel();
        var pump = new NotificationPump(link, attention, toasts, () => T.At(1));

        var count = await pump.PumpAsync();

        Assert.Equal(2, count);
        Assert.Equal(["toast", "overlay"], harness.Api.Claims[0]);
        var toast = Assert.Single(toasts.Shown);
        Assert.Equal("Nuovo ticket #261000001", toast.Title);
        var waiting = Assert.Single(attention.Items);
        Assert.Equal(Severity.Critical, waiting.Severity);
        Assert.Equal(new Uri("https://crm.example/t/1"), waiting.Url);
    }

    [Fact]
    public async Task WithoutAConnection_NothingIsCollected()
    {
        await using var link = new ServiceLink($"LiveHelpDeskTest-{Guid.NewGuid():N}");
        var toasts = new RecordingToasts();
        var pump = new NotificationPump(link, new AttentionModel(), toasts, () => T.At(1));

        Assert.Equal(0, await pump.PumpAsync());
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public void ANotificationForAnotherChannel_IsIgnored()
    {
        var toasts = new RecordingToasts();
        var attention = new AttentionModel();
        var pump = new NotificationPump(new ServiceLink("unused"), attention, toasts, () => T.At(1));

        pump.Deliver(new NotificationInfo(1, "k", "email", "t", "b", Severity.Info, null));

        Assert.Empty(toasts.Shown);
        Assert.Equal(0, attention.Count);
    }
}

public class ServiceLinkTests
{
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "the condition did not become true in time");
    }

    private static ServiceLink Link(string name) => new(name, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task ItConnects_AndExposesTheApi()
    {
        await using var harness = new PipeHarness();
        await using var link = Link(harness.Name);
        Assert.Null(link.Api);

        link.Start();
        await WaitForAsync(() => link.State == LinkState.Connected);

        Assert.Equal(446, (await link.Api!.GetStatusAsync()).ActiveTickets);
    }

    [Fact]
    public async Task WithoutAService_ItSaysSo_AndKeepsTrying()
    {
        var name = $"LiveHelpDeskTest-{Guid.NewGuid():N}";
        await using var link = Link(name);
        var states = new List<LinkState>();
        link.StateChanged += states.Add;

        link.Start();
        await WaitForAsync(() => link.State == LinkState.ServiceMissing);

        await using var harness = new PipeHarness(name: name); // the service shows up later
        await WaitForAsync(() => link.State == LinkState.Connected);
        Assert.Equal([LinkState.ServiceMissing, LinkState.Connected], states);
    }

    [Fact]
    public async Task WhenTheServiceRestarts_TheAppReconnectsByItself()
    {
        var name = $"LiveHelpDeskTest-{Guid.NewGuid():N}";
        var first = new PipeHarness(name: name);
        await using var link = Link(name);
        link.Start();
        await WaitForAsync(() => link.State == LinkState.Connected);

        await first.DisposeAsync();
        await WaitForAsync(() => link.State != LinkState.Connected);

        await using var second = new PipeHarness(name: name);
        await WaitForAsync(() => link.State == LinkState.Connected);
        Assert.Equal(446, (await link.Api!.GetStatusAsync()).ActiveTickets);
    }

    [Fact]
    public async Task ChangeNotices_AreForwarded()
    {
        await using var harness = new PipeHarness();
        await using var link = Link(harness.Name);
        link.Start();
        await WaitForAsync(() => link.State == LinkState.Connected);
        await link.Api!.GetStatusAsync();
        var received = new TaskCompletionSource<ChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Changed += (_, e) => received.TrySetResult(e.Kind);

        harness.Api.Raise(ChangeKind.Events);

        Assert.Equal(ChangeKind.Events, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

public class SmallAppPartsTests
{
    [Fact]
    public void AToastClick_IsReadBack_FromItsArguments()
    {
        var arguments = new ToastArguments().Add("action", "open").Add("key", "k1").Add("url", "https://crm.example/t/1").ToString();

        var action = ToastService.Parse(arguments);

        Assert.Equal(new ToastAction("open", "k1", new Uri("https://crm.example/t/1")), action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("action=open")]
    [InlineData("key=k1")]
    public void ArgumentsThatAreNotOurs_AreIgnored(string arguments) =>
        Assert.Null(ToastService.Parse(arguments));

    [Fact]
    public void ABadUrlInAToast_IsDroppedNotTrusted()
    {
        var arguments = new ToastArguments().Add("action", "open").Add("key", "k1").Add("url", "not a url").ToString();

        Assert.Null(ToastService.Parse(arguments)!.Url);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void TheOverlayYields_ToPresentationsAndFullScreenApps(int state, bool busy) =>
        Assert.Equal(busy, WindowsDoNotDisturb.IsBusyState(state));

    [Theory]
    [InlineData(LinkState.Connected, false, AttentionLevel.None, TrayLook.Ok)]
    [InlineData(LinkState.Connected, false, AttentionLevel.Attention, TrayLook.Attention)]
    [InlineData(LinkState.Connected, false, AttentionLevel.Critical, TrayLook.Alert)]
    [InlineData(LinkState.Connected, true, AttentionLevel.None, TrayLook.Alert)]
    [InlineData(LinkState.ServiceMissing, false, AttentionLevel.None, TrayLook.Alert)]
    [InlineData(LinkState.Connecting, false, AttentionLevel.None, TrayLook.Idle)]
    [InlineData(LinkState.Connecting, false, AttentionLevel.Attention, TrayLook.Attention)]
    public void TheTrayIcon_ReflectsTheWorstThing(LinkState link, bool needsLogin, AttentionLevel attention, TrayLook expected) =>
        Assert.Equal(expected, TrayLooks.For(link, needsLogin, attention));

    [Fact]
    public void Settings_AreKept_AndADamagedFileMeansDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lhd-ui-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new UiSettings { OverlayEnabled = false, OverlayOffsetX = -120, OverlayTop = 40 };
            settings.Save(path);

            var loaded = UiSettings.Load(path);
            Assert.False(loaded.OverlayEnabled);
            Assert.Equal(-120, loaded.OverlayOffsetX);
            Assert.Equal(40, loaded.OverlayTop);

            File.WriteAllText(path, "{ not json");
            Assert.True(UiSettings.Load(path).OverlayEnabled);
            Assert.True(UiSettings.Load(path + ".missing").OverlayEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("TicketCreated", null, "Nuovo ticket")]
    [InlineData("MessageReceived", "Email", "Nuova e-mail")]
    [InlineData("StatusChanged", "Reopened", "Riaperto")]
    [InlineData("MonitorHealth", "AuthExpired", "Accesso scaduto")]
    [InlineData("SomethingNew", "x", "SomethingNew / x")]
    public void Events_AreCalledInPlainItalian(string type, string? subtype, string expected) =>
        Assert.Equal(expected, EventLabels.Describe(type, subtype));

    [Fact]
    public async Task ASecondLaunch_AsksTheFirstToShowItself_AndDoesNotRun()
    {
        var name = $"LiveHelpDeskTest-{Guid.NewGuid():N}";
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = SingleInstance.TryAcquire(name, () => asked.TrySetResult());
        Assert.NotNull(first);

        var second = SingleInstance.TryAcquire(name, () => { });

        Assert.Null(second);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class AppViewModelTests
{
    private static OverlayViewModel Overlay() => new(() => Task.CompletedTask, () => Task.CompletedTask, _ => { });

    [Fact]
    public void TheOverlay_ShowsTheHeadline_AndHowManyMoreAreWaiting()
    {
        var model = new AttentionModel();
        model.Add(new AttentionItem("a", "Nuovo ticket #1", "errore fattura\nPriorità: Alta", Severity.Attention, null, T.At(0)), T.At(0));
        var vm = Overlay();

        vm.Update(model, T.At(1));
        Assert.Equal("Nuovo ticket #1", vm.Title);
        Assert.Equal("errore fattura", vm.Subtitle);
        Assert.Equal("1", vm.CountText);
        Assert.True(vm.IsAttention);

        model.Add(new AttentionItem("b", "Ticket #2 senza risposta", "da molto", Severity.Critical, null, T.At(2)), T.At(2));
        vm.Update(model, T.At(3));
        Assert.Equal("Ticket #2 senza risposta", vm.Title);
        Assert.Equal("da molto · +1 da gestire", vm.Subtitle);
        Assert.Equal("2", vm.CountText);
        Assert.True(vm.IsCritical);
        Assert.False(vm.IsAttention);
    }

    [Fact]
    public void ASnoozedOverlay_HasNoLevel()
    {
        var model = new AttentionModel();
        model.Add(new AttentionItem("a", "t", "b", Severity.Attention, null, T.At(0)), T.At(0));
        model.Snooze(TimeSpan.FromMinutes(15), T.At(0));
        var vm = Overlay();

        vm.Update(model, T.At(1));

        Assert.Equal(AttentionLevel.None, vm.Level);
    }

    [Fact]
    public void TheOverlayButtons_CallTheirActions()
    {
        var opened = 0;
        var acked = 0;
        TimeSpan? snoozed = null;
        var vm = new OverlayViewModel(() => { opened++; return Task.CompletedTask; }, () => { acked++; return Task.CompletedTask; }, d => snoozed = d);

        vm.OpenCommand.Execute(null);
        vm.AcknowledgeAllCommand.Execute(null);
        vm.SnoozeQuarterHourCommand.Execute(null);

        Assert.Equal((1, 1), (opened, acked));
        Assert.Equal(TimeSpan.FromMinutes(15), snoozed);
    }

    private static StatusViewModel Status(FakeApi? api = null, List<Uri>? opened = null, List<string>? copied = null) =>
        new(() => api, text => { copied?.Add(text); return Task.CompletedTask; }, url => opened?.Add(url));

    [Theory]
    [InlineData(ProfileState.NeedsLogin, "bad", true, false)]
    [InlineData(ProfileState.LoggingIn, "warn", true, true)]
    [InlineData(ProfileState.Starting, "warn", false, false)]
    [InlineData(ProfileState.Degraded, "warn", false, false)]
    [InlineData(ProfileState.Running, "ok", false, false)]
    public void EachServiceState_HasItsColour_AndShowsTheLoginOnlyWhenNeeded(ProfileState state, string kind, bool showLogin, bool pending)
    {
        var vm = Status();

        vm.Apply(new FakeApi().Status with { State = state });

        Assert.Equal(kind, vm.Kind);
        Assert.Equal(showLogin, vm.ShowLogin);
        Assert.Equal(pending, vm.LoginPending);
    }

    [Fact]
    public async Task WithoutTheService_TheStatusExplainsIt()
    {
        var vm = Status();

        await vm.RefreshAsync(LinkState.ServiceMissing);

        Assert.Contains("non è in esecuzione", vm.Headline);
        Assert.Equal("bad", vm.Kind);
        Assert.False(vm.ShowLogin);
    }

    [Fact]
    public async Task StartingTheLogin_ShowsTheCode_AndOpensTheBrowser()
    {
        var api = new FakeApi { Status = new FakeApi().Status with { State = ProfileState.NeedsLogin } };
        var opened = new List<Uri>();
        var copied = new List<string>();
        var vm = Status(api, opened, copied);
        vm.Apply(api.Status);

        await vm.StartLoginCommand.ExecuteAsync(null);
        await vm.CopyCodeCommand.ExecuteAsync(null);

        Assert.Equal("ABC123", vm.LoginCode);
        Assert.True(vm.LoginPending);
        Assert.Contains("Windows Hello", vm.LoginHelp);
        Assert.Equal([new Uri("https://microsoft.com/devicelogin")], opened);
        Assert.Equal(["ABC123"], copied);
    }

    [Fact]
    public async Task TheChannelList_ComesFromTheService_AndEachCanBeTested()
    {
        var api = new FakeApi();
        var vm = Status(api);

        await vm.RefreshAsync(LinkState.Connected);

        Assert.Equal(["E-mail", "SMS"], vm.Channels.Select(c => c.Name).ToArray());
        Assert.False(vm.Channels[1].Enabled);
        Assert.Contains("ultimo invio", vm.Channels[0].Detail);
        await vm.Channels[0].TestCommand.ExecuteAsync(null);
        Assert.Equal("Messaggio di prova inviato.", vm.Channels[0].TestResult);
        Assert.Equal(["email"], api.Tested);
    }

    [Fact]
    public async Task TheEventList_ShowsWhatTheServiceReports_AndAcknowledgingMarksTheRow()
    {
        var api = new FakeApi();
        var acknowledged = new List<string>();
        var vm = new EventsViewModel(() => api, _ => Task.CompletedTask, key => { acknowledged.Add(key); return Task.CompletedTask; });

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal("Nuovo ticket", row.Label);
        Assert.Equal("#261000001", row.Number);
        Assert.Equal("", vm.Message);
        Assert.True(row.CanAcknowledge);

        await row.AcknowledgeCommand.ExecuteAsync(null);

        Assert.Equal(["k1"], acknowledged);
        Assert.True(row.Acknowledged);
        Assert.False(row.CanAcknowledge);
    }

    [Fact]
    public async Task WithoutTheService_TheEventListSaysSo()
    {
        var vm = new EventsViewModel(() => null, _ => Task.CompletedTask, _ => Task.CompletedTask);

        await vm.RefreshAsync();

        Assert.Contains("non è raggiungibile", vm.Message);
        Assert.Empty(vm.Rows);
    }
}
