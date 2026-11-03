using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.App.Views;
using LiveHelpDesk.Dynamics;
using LiveHelpDesk.Ipc;
using Xunit;

#pragma warning disable CS0618 // Bitmap.Save(path)

namespace LiveHelpDesk.Tests;

/// <summary>Off-screen renderings of the configuration views (set LHD_SHOTS to keep the images).</summary>
public class Phase6UiTests
{
    private static void Save(WriteableBitmap frame, string name)
    {
        if (Environment.GetEnvironmentVariable("LHD_SHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name + ".png"));
        }
    }

    private static IEnumerable<string> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0);

    private static Window Show(Control view, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public async Task TheRulesEditor_ShowsTheRuleList_AndTheFormOfTheSelectedOne()
    {
        var vm = new RulesViewModel(() => new FakeApi());
        await vm.LoadAsync();
        var window = Show(new RulesView { DataContext = vm }, 1100, 980);

        var texts = Texts(window).ToList();
        Assert.Contains("Regole di notifica", texts);
        Assert.Contains("Nuovi ticket", texts);
        Assert.Contains("Messaggi e risposte dei clienti", texts);
        Assert.Contains("Quando", texts);
        Assert.Contains("Allora avvisa tramite", texts);
        Assert.Contains("Nuovo ticket", texts); // an event checkbox
        Assert.Contains("Notifica di Windows", texts);
        Save(window.CaptureRenderedFrame()!, "rules-form");
    }

    [AvaloniaFact]
    public async Task ARuleWithConditions_ShowsOneRowPerCondition()
    {
        var api = new FakeApi
        {
            RulesJson = """
            { "version": 1, "rules": [
              { "id": "urgenti", "name": "Urgenti di BFA", "events": ["TicketCreated", "CustomerReplied"],
                "actions": { "channels": ["toast", "overlay", "sms"], "severity": "Critical", "detail": "Minimal", "escalateAfterMinutes": 20, "escalateChannels": ["email"] },
                "when": { "all": [ { "field": "priority", "op": "eq", "value": 1 }, { "field": "queue", "op": "eq", "value": "bbbbbbbb-0000-0000-0000-000000000002" } ],
                          "any": [ { "field": "title", "op": "contains", "value": "BFA" }, { "field": "message.from", "op": "contains", "value": "@bfa.it" } ],
                          "not": { "field": "onHold", "op": "eq", "value": true } } } ] }
            """,
        };
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();
        var window = Show(new RulesView { DataContext = vm }, 1100, 1100);

        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.True(combos.Count >= 10); // five rows with two or three drop-downs each, plus urgency and detail
        Assert.Contains("Alta", combos.Select(c => c.SelectedItem?.ToString()));
        Assert.Contains("Assistenza L1", combos.Select(c => c.SelectedItem?.ToString()));
        Assert.Contains("Devono valere tutte:", Texts(window));
        Assert.Contains("Basta che ne valga una:", Texts(window));
        Assert.Contains("Non deve valere nessuna:", Texts(window));
        Save(window.CaptureRenderedFrame()!, "rules-conditions");
    }

    [AvaloniaFact]
    public async Task ThePreviewAndTheErrors_AppearAboveTheEditor()
    {
        var api = new FakeApi();
        var vm = new RulesViewModel(() => api);
        await vm.LoadAsync();
        await vm.PreviewCommand.ExecuteAsync(null);
        foreach (var e in vm.Selected!.EventChoices)
        {
            e.IsSelected = false;
        }

        await vm.SaveCommand.ExecuteAsync(null);
        var window = Show(new RulesView { DataContext = vm }, 1100, 980);

        var texts = Texts(window).ToList();
        Assert.Contains("2 ticket", texts);
        Assert.Contains("nessun ticket", texts);
        Assert.Contains(texts, t => t.Contains("nessun evento"));
        Assert.Contains("Ci sono modifiche non salvate.", texts);
        Save(window.CaptureRenderedFrame()!, "rules-preview-errors");
    }

    [AvaloniaFact]
    public async Task TheTextView_ShowsTheRulesFile()
    {
        var vm = new RulesViewModel(() => new FakeApi());
        await vm.LoadAsync();
        vm.ToggleJsonCommand.Execute(null);
        var window = Show(new RulesView { DataContext = vm }, 1000, 700);

        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => (t.Text ?? "").Contains("\"new-tickets\""));
        Assert.Contains("Applica al modulo", window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()));
        Save(window.CaptureRenderedFrame()!, "rules-json");
    }

    [AvaloniaFact]
    public async Task TheChannelsForm_ShowsFourCards_WithMaskedSecretsAndTestButtons()
    {
        var vm = new ChannelsViewModel(() => new FakeApi());
        await vm.LoadAsync();
        var window = Show(new ChannelsView { DataContext = vm }, 980, 1500);

        var texts = Texts(window).ToList();
        Assert.Contains("Canali di notifica", texts);
        Assert.Contains("Server SMTP", texts);
        Assert.Contains("Indirizzo (webhook)", texts);
        Assert.Contains("Modello approvato", texts);
        var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Contains(boxes, b => b.Text == "********" && b.PasswordChar == '●');
        Assert.Equal(4, window.GetVisualDescendants().OfType<Button>().Count(b => b.Content?.ToString() == "Invia un messaggio di prova"));
        Save(window.CaptureRenderedFrame()!, "channels");
    }

    [AvaloniaFact]
    public async Task TheEmailCard_WithOutlook_ShowsTheSignInAndHidesTheSmtpFields()
    {
        var api = new FakeApi { Settings = new ChannelSettings { Email = { Enabled = true, Provider = EmailProvider.Outlook } } };
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();
        var window = Show(new ChannelsView { DataContext = vm }, 980, 1500);

        Assert.Contains("Account Outlook", VisibleTexts(window));
        Assert.Contains("Accedi a Outlook", VisibleButtons(window));
        Assert.DoesNotContain("Server SMTP", VisibleTexts(window));
        Save(window.CaptureRenderedFrame()!, "channels-outlook");

        await vm.Email!.Session.SignInCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("mario@qgs.eu", VisibleTexts(window));
        Assert.Contains("Esci", VisibleButtons(window));
        Assert.DoesNotContain("Accedi a Outlook", VisibleButtons(window));
    }

    private static List<string?> VisibleTexts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    private static List<string?> VisibleButtons(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content?.ToString())];

    [AvaloniaFact]
    public void TheSettingsView_ShowsTheOverlayChoices()
    {
        var vm = new SettingsViewModel(
            new UiSettings(), new AutoStart(@"Software\LiveHelpDeskTests\ui", "x"), "LiveHelpDesk.exe",
            ["Schermo 1 (principale) · 1920×1080", "Schermo 2 · 1366×768"], () => { }, () => { });
        var window = Show(new SettingsView { DataContext = vm }, 800, 520);

        var texts = Texts(window).ToList();
        Assert.Contains("Impostazioni", texts);
        Assert.Contains("Finestrella lampeggiante", texts);
        Assert.Equal("Lo schermo principale", window.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem?.ToString());
        Save(window.CaptureRenderedFrame()!, "settings");
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\LiveHelpDeskTests", throwOnMissingSubKey: false);
    }
}

public class Phase6ServiceTests
{
    private static readonly Uri Api = new("https://org.crm4.dynamics.com/api/data/v9.2/");

    [Fact]
    public async Task NamesBehindIds_AreLookedUpInQueuesTeamsAndUsers_AndUnknownOnesAreLeftOut()
    {
        var queue = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var user = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var stranger = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("queues") => FakeHandler.Json($$"""{ "value": [ { "queueid": "{{queue}}", "name": "Assistenza L1" } ] }"""),
            var p when p.EndsWith("systemusers") => FakeHandler.Json($$"""{ "value": [ { "systemuserid": "{{user}}", "fullname": "Mario Rossi" } ] }"""),
            _ => FakeHandler.Json("""{ "value": [] }"""),
        });
        var source = new DynamicsTicketSource(new DataverseClient(new HttpClient(handler) { BaseAddress = Api }, new FakeTokens()), DynamicsFieldMap.Qgs, () => null);

        var found = await source.ResolveNamesAsync([queue, user, stranger, queue], CancellationToken.None);

        Assert.Equal(
            [(queue, "Assistenza L1", "queue"), (user, "Mario Rossi", "user")],
            found.OrderBy(f => f.Kind).ToArray());
        Assert.Equal(3, handler.Requests.Count); // one query per kind, each with every id
        Assert.Contains($"queueid eq {queue:D}", Uri.UnescapeDataString(handler.Requests[0].Uri.ToString()));
        Assert.Contains($"queueid eq {stranger:D}", Uri.UnescapeDataString(handler.Requests[0].Uri.ToString()));
    }

    [Fact]
    public async Task TheRulesCalls_TravelThePipe()
    {
        await using var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();

        var json = await connection.Api.GetRulesJsonAsync();
        var saved = await connection.Api.SaveRulesJsonAsync(json);
        var rejected = await connection.Api.SaveRulesJsonAsync("{ rotto");
        var dry = await connection.Api.DryRunDraftAsync(json);
        var lookups = await connection.Api.GetLookupsAsync();

        Assert.Contains("new-tickets", json);
        Assert.True(saved.Saved);
        Assert.False(rejected.Saved);
        Assert.NotEmpty(rejected.Errors);
        Assert.Equal(446, dry.TicketsEvaluated);
        Assert.Equal(2, dry.Rules[0].Matches);
        Assert.Equal(["Assistenza L1", "Mario Rossi"], lookups.Select(l => l.Name).ToArray());
        Assert.Equal("queue", lookups[0].Kind);
    }
}
