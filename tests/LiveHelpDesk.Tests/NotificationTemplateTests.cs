using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.App.Views;
using LiveHelpDesk.Channels;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Notifications;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;
using Xunit;

namespace LiveHelpDesk.Tests;

public class TemplatesConfigTests
{
    private static NotificationTemplate Template(string id, string[] rules, string[] channels, string title = "{{evento}}", string body = "{{titolo}}") =>
        new() { Id = id, Name = id, RuleIds = [.. rules], Channels = [.. channels], Title = title, Body = body };

    [Fact]
    public void AValidFile_RoundTrips()
    {
        var file = new TemplatesFile { Templates = [Template("a", ["new-tickets"], ["email", "teams"])] };

        var result = TemplatesConfig.Parse(TemplatesConfig.Serialize(file));

        Assert.True(result.IsValid);
        var template = Assert.Single(result.File!.Templates);
        Assert.Equal(["email", "teams"], template.Channels);
        Assert.Equal("{{titolo}}", template.Body);
    }

    [Fact]
    public void ARuleOnAChannel_CanHaveOnlyOneText()
    {
        var file = new TemplatesFile
        {
            Templates = [Template("a", ["new-tickets"], ["email", "sms"]), Template("b", ["NEW-TICKETS"], ["SMS"])],
        };

        var error = Assert.Single(TemplatesConfig.Validate(file));

        Assert.Contains("'b'", error);
        Assert.Contains("'a'", error);
    }

    [Theory]
    [InlineData("{{evento}}", "Ciao {{nome_che_non_esiste}}", "campo sconosciuto")]
    [InlineData("{{evento}}", "Ciao {{numero}", "graffe non chiuse")]
    [InlineData("", " ", "vuoti")]
    [InlineData("riga\nriga", "x", "una riga")]
    public void Mistakes_AreExplained(string title, string body, string expected)
    {
        var errors = TemplatesConfig.Validate(new TemplatesFile { Templates = [Template("a", ["r"], ["toast"], title, body)] });

        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void NoRulesOrChannels_IsAMistake()
    {
        var errors = TemplatesConfig.Validate(new TemplatesFile { Templates = [Template("a", [], [])] });

        Assert.Contains(errors, e => e.Contains("nessuna regola"));
        Assert.Contains(errors, e => e.Contains("nessun canale"));
    }

    [Fact]
    public void ANewerVersion_IsRefused() =>
        Assert.False(TemplatesConfig.Parse("""{ "version": 99, "templates": [] }""").IsValid);
}

public class TemplateRendererTests
{
    private static readonly Dictionary<string, string?> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["numero"] = "261000001",
        ["titolo"] = "Errore *grave* <b>",
        ["link"] = "https://crm.example/t?id=1&x=2",
        ["mittente"] = null,
    };

    private const string Body = "**{{titolo}}**\n\n- uno\n- due\n\n[Apri]({{link}})";

    [Fact]
    public void Plain_DropsTheMarkup_AndSpellsOutTheLink()
    {
        var text = TemplateRenderer.RenderBody(Body, Values, TemplateFormat.Plain);

        Assert.Equal("Errore *grave* <b>\n• uno\n• due\nApri (https://crm.example/t?id=1&x=2)", text);
    }

    [Fact]
    public void Markdown_KeepsTheFormattingTeamsUnderstands()
    {
        var text = TemplateRenderer.RenderBody(Body, Values, TemplateFormat.Markdown);

        Assert.StartsWith("**Errore \\*grave\\* <b>**", text);
        Assert.Contains("- uno", text);
        Assert.Contains("[Apri](https://crm.example/t?id=1&x=2)", text);
    }

    [Fact]
    public void Html_EscapesTheValues_SoATitleCannotInjectMarkup()
    {
        var html = TemplateRenderer.RenderBody(Body, Values, TemplateFormat.Html);

        Assert.Contains("<strong>Errore *grave* &lt;b&gt;</strong>", html);
        Assert.Contains("<li>uno</li>", html);
        Assert.Contains("<a href=\"https://crm.example/t?id=1&amp;x=2\">Apri</a>", html);
        Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void ABareLink_BecomesALinkToo()
    {
        var html = TemplateRenderer.RenderBody("Vai a {{link}}", Values, TemplateFormat.Html);

        Assert.Contains("href=\"https://crm.example/t?id=1&amp;x=2\"", html);
    }

    [Fact]
    public void EmptyValues_AndUnknownTags()
    {
        Assert.Equal("Da:", TemplateRenderer.RenderTitle("Da: {{mittente}}", Values));
        Assert.Equal("{{boh}} 261000001", TemplateRenderer.RenderTitle("{{boh}} {{ numero }}", Values));
    }

    [Fact]
    public void TheTitle_StaysOnOneLine() =>
        Assert.Equal("a b", TemplateRenderer.RenderTitle("a\n\nb", Values));

    [Fact]
    public void EveryFieldHasASample_AndPassesTheCheck()
    {
        foreach (var field in TemplateFields.All)
        {
            Assert.Empty(TemplateRenderer.CheckTags($"{{{{{field.Key}}}}}"));
            Assert.False(string.IsNullOrEmpty(field.Sample));
        }
    }
}

public class TemplatedNotificationTests
{
    private static readonly Uri Link = new("https://crm.example/t/1");

    private static RuleMatch Match(params string[] channels) =>
        new(
            new Rule { Id = "r", Name = "Le mie", Events = [TicketEventType.TicketCreated], Actions = new RuleActions { Channels = [.. channels] } },
            new TicketEvent("k", TicketEventType.TicketCreated, T.At(0), T.Id(1), "261000001", "t", Ticket: T.Ticket(priority: 1, title: "Stampa ferma")));

    private static IReadOnlyList<OutboxItem> Plan(NotificationTemplate template, params string[] channels) =>
        new NotificationPlanner(TicketMapping.Qgs, () => new ChannelPolicy(), _ => Link, () => [template]).Plan(Match(channels), T.At(0));

    [Fact]
    public void EachChannel_GetsTheTextInItsOwnFormat_AndTheOthersKeepTheStandardOne()
    {
        var template = new NotificationTemplate
        {
            Id = "a",
            RuleIds = ["R"],
            Channels = ["email", "teams", "sms"],
            Title = "[{{regola}}] {{numero}}",
            Body = "**{{titolo}}** · {{priorita}}",
        };

        var items = Plan(template, "email", "teams", "sms", "toast");

        var email = items.Single(i => i.Channel == "email").Notification;
        Assert.Equal("[Le mie] 261000001", email.Title);
        Assert.Equal("Stampa ferma · Alta", email.Body);
        Assert.Equal("<p><strong>Stampa ferma</strong> · Alta</p>", email.HtmlBody);
        Assert.Equal("**Stampa ferma** · Alta", items.Single(i => i.Channel == "teams").Notification.Body);
        Assert.Equal("Stampa ferma · Alta", items.Single(i => i.Channel == "sms").Notification.Body);

        var toast = items.Single(i => i.Channel == "toast").Notification;
        Assert.Equal("Nuovo ticket #261000001", toast.Title);
        Assert.Null(toast.HtmlBody);
    }

    [Fact]
    public void AnEmptyTitleOrBody_FallsBackToTheStandardOne()
    {
        var template = new NotificationTemplate { Id = "a", RuleIds = ["r"], Channels = ["toast"], Body = "Solo {{numero}}" };

        var toast = Plan(template, "toast").Single().Notification;

        Assert.Equal("Nuovo ticket #261000001", toast.Title);
        Assert.Equal("Solo 261000001", toast.Body);
        Assert.Equal(Link, toast.Url);
    }

    [Fact]
    public void AnotherRulesText_IsNotUsed()
    {
        var template = new NotificationTemplate { Id = "a", RuleIds = ["altra"], Channels = ["toast"], Body = "x" };

        Assert.NotEqual("x", Plan(template, "toast").Single().Notification.Body);
    }

    [Fact]
    public async Task TheEmail_IsHtml_AndDoesNotRepeatALinkTheTextAlreadyHas()
    {
        var sender = new RecordingEmailSender();
        var settings = new EmailSettings { Enabled = true, Host = "smtp.example.com", From = "h@example.com", To = ["a@example.com"] };
        var template = new NotificationTemplate { Id = "a", RuleIds = ["r"], Channels = ["email"], Body = "**{{titolo}}**\n\n[Apri il ticket]({{link}})" };

        await new EmailChannel(settings, sender).SendAsync(Plan(template, "email").Single().Notification, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.NotNull(message.HtmlBody);
        Assert.StartsWith("<!DOCTYPE html>", message.HtmlBody);
        Assert.Contains("<strong>Stampa ferma</strong>", message.HtmlBody);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(message.HtmlBody!, "href="));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(message.TextBody, "crm.example/t/1"));
    }

    [Fact]
    public async Task WithoutATemplate_TheEmailIsPlainText_AsBefore()
    {
        var sender = new RecordingEmailSender();
        var settings = new EmailSettings { Enabled = true, Host = "smtp.example.com", From = "h@example.com", To = ["a@example.com"] };

        await new EmailChannel(settings, sender).SendAsync(new Notification("t", "b", Severity.Info, Link), CancellationToken.None);

        Assert.Null(sender.Sent[0].HtmlBody);
        Assert.EndsWith("Apri il ticket: https://crm.example/t/1", sender.Sent[0].TextBody);
    }
}

public class NotificationsPageTests
{
    private static async Task<(NotificationsViewModel Vm, FakeApi Api)> LoadedAsync(params NotificationTemplate[] templates)
    {
        var api = new FakeApi { TemplatesJson = TemplatesConfig.Serialize(new TemplatesFile { Templates = [.. templates] }) };
        var vm = new NotificationsViewModel(() => api);
        await vm.LoadAsync();
        return (vm, api);
    }

    [AvaloniaFact]
    public async Task ANewText_IsSavedForTheChosenRulesAndChannels()
    {
        var (vm, api) = await LoadedAsync();
        Assert.Null(vm.Selected);

        vm.AddTemplateCommand.Execute(null);
        var template = vm.Selected!;
        Assert.True(vm.IsDirty);
        Assert.NotEmpty(template.RuleChoices);
        template.RuleChoices[0].IsSelected = true;
        template.ChannelChoices.Single(c => c.Value == "email").IsSelected = true;
        template.TitleDocument.Text = "Ticket {{numero}}";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        Assert.Empty(vm.Errors);
        var saved = Assert.Single(TemplatesConfig.Parse(api.SavedTemplates!).File!.Templates);
        Assert.Equal([template.RuleChoices[0].Value], saved.RuleIds);
        Assert.Equal(["email"], saved.Channels);
        Assert.Equal("Ticket {{numero}}", saved.Title);
        Assert.Equal(NotificationsViewModel.DefaultBody, saved.Body);
    }

    [AvaloniaFact]
    public async Task AnOverlap_IsShownWhileEditing_AndNotSaved()
    {
        var (vm, api) = await LoadedAsync(
            new NotificationTemplate { Id = "a", Name = "Primo", RuleIds = ["new-tickets"], Channels = ["email"], Body = "x" });

        vm.DuplicateTemplateCommand.Execute(null);
        Assert.Equal("Primo (copia)", vm.Selected!.Name);
        Assert.False(vm.HasConflicts);
        vm.Selected.ChannelChoices.Single(c => c.Value == "email").IsSelected = true;
        Assert.True(vm.HasConflicts);
        Assert.Contains("Primo", vm.Conflicts);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(api.SavedTemplates);
        Assert.True(vm.HasErrors);
    }

    [AvaloniaFact]
    public async Task ChannelsNotInUse_AreHidden_UnlessAlreadyChosen()
    {
        var (vm, _) = await LoadedAsync(
            new NotificationTemplate { Id = "a", Name = "Vecchio", RuleIds = ["new-tickets"], Channels = ["sms"], Body = "x" },
            new NotificationTemplate { Id = "b", Name = "Nuovo", RuleIds = ["new-tickets"], Channels = ["email"], Body = "y" });

        Assert.True(vm.Templates[0].ChannelChoices.Single(c => c.Value == "sms").IsSelected);
        Assert.DoesNotContain(vm.Templates[0].ChannelChoices, c => c.Value == "whatsapp");
        Assert.DoesNotContain(vm.Templates[1].ChannelChoices, c => c.Value is "sms" or "whatsapp");
    }

    [AvaloniaFact]
    public async Task ThePreview_FollowsTheChannel()
    {
        var (vm, _) = await LoadedAsync(
            new NotificationTemplate { Id = "a", Name = "T", RuleIds = ["new-tickets"], Channels = ["teams", "sms"], Title = "{{numero}}", Body = "**{{priorita}}**" });

        Assert.Equal("teams", vm.PreviewChannel!.Value);
        Assert.True(vm.IsRichPreview);
        Assert.Equal("CAS-01234-X1Y2Z3", vm.PreviewTitle);
        Assert.Equal("**Alta**", vm.PreviewBody);

        Assert.DoesNotContain(vm.PreviewChannels, c => c.Value is "sms" or "whatsapp");
        vm.PreviewChannel = vm.PreviewChannels.Single(c => c.Value == "toast");
        Assert.False(vm.IsRichPreview);
        Assert.Equal("Alta", vm.PreviewBody);

        vm.Selected!.BodyDocument.Text = "{{regola}}";
        Assert.Equal("Nuovi ticket", vm.PreviewBody);
    }

    [AvaloniaFact]
    public async Task Removing_AndDiscarding()
    {
        var (vm, api) = await LoadedAsync(
            new NotificationTemplate { Id = "a", Name = "T", RuleIds = ["new-tickets"], Channels = ["sms"], Body = "x" });

        vm.RemoveTemplateCommand.Execute(null);
        Assert.Empty(vm.Templates);
        Assert.True(vm.IsDirty);

        await vm.DiscardCommand.ExecuteAsync(null);
        Assert.Single(vm.Templates);
        Assert.False(vm.IsDirty);
        Assert.Null(api.SavedTemplates);
    }

    [AvaloniaFact]
    public async Task ADeletedRule_IsReported_AndDroppedOnSave()
    {
        var (vm, api) = await LoadedAsync(
            new NotificationTemplate { Id = "a", Name = "T", RuleIds = ["new-tickets", "sparita"], Channels = ["sms"], Body = "x" });

        Assert.True(vm.Selected!.HasWarning);
        Assert.Contains("sparita", vm.Selected.Warning);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["new-tickets"], TemplatesConfig.Parse(api.SavedTemplates!).File!.Templates[0].RuleIds);
    }

    [AvaloniaFact]
    public async Task ThePage_ShowsTheTextInTheEditors_AndARenderedPreview()
    {
        var (vm, _) = await LoadedAsync(
            new NotificationTemplate { Id = "a", Name = "Per Teams", RuleIds = ["new-tickets"], Channels = ["teams"], Title = "{{numero}}", Body = "**{{priorita}}** e _{{stato}}_" });
        var window = new Window { Width = 1000, Height = 900, Content = new NotificationsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var editors = window.GetVisualDescendants().OfType<TextEditor>().ToList();
        Assert.Equal(2, editors.Count);
        Assert.Equal("{{numero}}", editors[0].Text);
        Assert.Equal("**{{priorita}}** e _{{stato}}_", editors[1].Text);

        var preview = window.GetVisualDescendants().OfType<MarkdownPreview>().Single();
        Assert.True(preview.IsEffectivelyVisible);
        Assert.Equal("Alta e L1 In risoluzione", preview.Inlines!.Text);
        AppUiTests.Save(window.CaptureRenderedFrame()!, "notifications");

        vm.AddTemplateCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(NotificationsViewModel.DefaultTitle, editors[0].Text);
        Assert.Equal(NotificationsViewModel.DefaultBody, editors[1].Text.ReplaceLineEndings("\n"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TypingTwoBraces_OffersTheFields()
    {
        var (vm, _) = await LoadedAsync(
            new NotificationTemplate { Id = "a", Name = "T", RuleIds = ["new-tickets"], Channels = ["sms"], Title = "x", Body = "Ticket " });
        var window = new Window { Width = 1000, Height = 900, Content = new NotificationsView { DataContext = vm } };
        window.Show();
        var body = window.GetVisualDescendants().OfType<TextEditor>().Last();
        body.TextArea.Focus();
        body.CaretOffset = body.Document.TextLength;
        Dispatcher.UIThread.RunJobs();

        foreach (var c in "{{num")
        {
            window.KeyTextInput(c.ToString());
            Dispatcher.UIThread.RunJobs();
        }

        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Ticket {{numero}}", body.Text);
        window.Close();
    }
}
