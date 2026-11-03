using System.Net;
using System.Text;
using System.Text.Json;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.Channels;
using LiveHelpDesk.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveHelpDesk.Tests;

internal sealed class FakeMailTokens : IMailTokenSource
{
    public string? Account { get; set; } = "mario@qgs.eu";

    public List<bool> Requests { get; } = [];

    public bool KnownSignedOut => Account is null;

    public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Requests.Add(forceRefresh);
        return Account is null
            ? throw new MailAuthException(MailAuthFailure.AuthRequired, "Serve l'accesso a Outlook")
            : Task.FromResult(forceRefresh ? "token-2" : "token-1");
    }

    public Task<string?> AccountNameAsync(CancellationToken cancellationToken) => Task.FromResult(Account);
}

/// <summary>Answers each call with the next scripted response and remembers what was sent.</summary>
internal sealed class ScriptedGraph(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _next;

    public List<(Uri? Uri, string? Authorization, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request.RequestUri, request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
        return responses[Math.Min(_next++, responses.Length - 1)]();
    }
}

public class GraphMailSenderTests
{
    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted);

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message } }), Encoding.UTF8, "application/json") };

    private static GraphMailSender Sender(ScriptedGraph graph, FakeMailTokens tokens, bool saveCopy = false, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(new HttpClient(graph), tokens, () => saveCopy, delay ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public async Task AMessage_IsPostedToGraph_WithTheBearerToken()
    {
        var graph = new ScriptedGraph(Accepted);
        var tokens = new FakeMailTokens();

        await Sender(graph, tokens).SendAsync(new EmailMessage("ignored@x.it", ["boss@qgs.eu", "me@qgs.eu"], "[URGENTE] Ticket", "Corpo"), default);

        var call = Assert.Single(graph.Calls);
        Assert.Equal(GraphMailSender.SendMailUri, call.Uri);
        Assert.Equal("Bearer token-1", call.Authorization);
        using var json = JsonDocument.Parse(call.Body);
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("[URGENTE] Ticket", message.GetProperty("subject").GetString());
        Assert.Equal("Text", message.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("Corpo", message.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal(
            ["boss@qgs.eu", "me@qgs.eu"],
            message.GetProperty("toRecipients").EnumerateArray().Select(r => r.GetProperty("emailAddress").GetProperty("address").GetString()).OfType<string>().ToArray());
        Assert.False(json.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public async Task WithoutRecipients_TheMessageGoesToTheSignedInAccount()
    {
        var graph = new ScriptedGraph(Accepted);

        await Sender(graph, new FakeMailTokens { Account = "mario@qgs.eu" }, saveCopy: true).SendAsync(new EmailMessage("", [], "Oggetto", "Corpo"), default);

        using var json = JsonDocument.Parse(Assert.Single(graph.Calls).Body);
        Assert.Equal("mario@qgs.eu", json.RootElement.GetProperty("message").GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.True(json.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public async Task ARejectedToken_IsRenewedOnce_AndTheSendRetried()
    {
        var graph = new ScriptedGraph(() => Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "scaduto"), Accepted);
        var tokens = new FakeMailTokens();

        await Sender(graph, tokens).SendAsync(new EmailMessage("", ["a@b.it"], "S", "B"), default);

        Assert.Equal([false, true], tokens.Requests);
        Assert.Equal("Bearer token-2", graph.Calls[1].Authorization);
    }

    [Fact]
    public async Task ARepeated401_GivesUp_InsteadOfLooping()
    {
        var graph = new ScriptedGraph(() => Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "no"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sender(graph, new FakeMailTokens()).SendAsync(new EmailMessage("", ["a@b.it"], "S", "B"), default));

        Assert.Equal(2, graph.Calls.Count);
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task AForbidden_SaysTheMailSendPermissionIsMissing()
    {
        var graph = new ScriptedGraph(() => Error(HttpStatusCode.Forbidden, "ErrorAccessDenied", "Access is denied"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sender(graph, new FakeMailTokens()).SendAsync(new EmailMessage("", ["a@b.it"], "S", "B"), default));

        Assert.Contains("Mail.Send", ex.Message);
        Assert.Contains("ErrorAccessDenied", ex.Message);
        Assert.Single(graph.Calls);
    }

    [Fact]
    public async Task AShortThrottle_IsWaitedOutAndRetriedOnce()
    {
        var waits = new List<TimeSpan>();
        var graph = new ScriptedGraph(
            () =>
            {
                var response = Error((HttpStatusCode)429, "TooManyRequests", "piano");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return response;
            },
            Accepted);

        await Sender(graph, new FakeMailTokens(), delay: (span, _) =>
        {
            waits.Add(span);
            return Task.CompletedTask;
        }).SendAsync(new EmailMessage("", ["a@b.it"], "S", "B"), default);

        Assert.Equal([TimeSpan.FromSeconds(3)], waits);
        Assert.Equal(2, graph.Calls.Count);
    }

    [Fact]
    public async Task ALongThrottle_IsReportedNotWaitedOut()
    {
        var graph = new ScriptedGraph(() =>
        {
            var response = Error(HttpStatusCode.ServiceUnavailable, "ServiceNotAvailable", "occupato");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return response;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sender(graph, new FakeMailTokens()).SendAsync(new EmailMessage("", ["a@b.it"], "S", "B"), default));

        Assert.Single(graph.Calls);
        Assert.Contains("sovraccarico", ex.Message);
    }

    [Fact]
    public async Task WithoutASignIn_NothingIsSent()
    {
        var graph = new ScriptedGraph(Accepted);

        await Assert.ThrowsAsync<MailAuthException>(
            () => Sender(graph, new FakeMailTokens { Account = null }).SendAsync(new EmailMessage("", ["a@b.it"], "S", "B"), default));

        Assert.Empty(graph.Calls);
    }
}

public class OutlookChannelTests
{
    private static ChannelSettings Outlook(params string[] to) => new()
    {
        Email = { Enabled = true, Provider = EmailProvider.Outlook, To = [.. to] },
    };

    [Fact]
    public async Task TheOutlookProvider_NeedsNoServerOrSender()
    {
        var sender = new RecordingEmailSender();
        var channel = new EmailChannel(Outlook().Email, sender);

        var result = await channel.SendAsync(new Notification("Titolo", "Corpo", Severity.Info), default);

        Assert.True(result.Success);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task TheSmtpProvider_StillNeedsServerSenderAndRecipients()
    {
        var channel = new EmailChannel(new EmailSettings { Enabled = true }, new RecordingEmailSender());

        var result = await channel.SendAsync(new Notification("Titolo", "Corpo", Severity.Info), default);

        Assert.False(result.Success);
        Assert.Contains("incompleta", result.Error);
    }

    [Fact]
    public async Task ASignInProblem_ComesBackAsAFailureWithItsOwnMessage()
    {
        var channel = new EmailChannel(
            Outlook().Email,
            new RecordingEmailSender(new MailAuthException(MailAuthFailure.AuthRequired, "Serve l'accesso a Outlook")));

        var result = await channel.SendAsync(new Notification("T", "B", Severity.Info), default);

        Assert.False(result.Success);
        Assert.Equal("Serve l'accesso a Outlook", result.Error);
    }

    [Fact]
    public void TheFactory_BuildsTheOutlookChannel_WithoutNeedingAnSmtpHost()
    {
        var channels = ChannelFactory.Create(Outlook("a@qgs.eu"), new HttpClient(), outlookTokens: new FakeMailTokens());

        Assert.IsType<EmailChannel>(channels["email"]);
    }

    [Fact]
    public async Task TheFactory_SendsThroughGraph_WhenTheProviderIsOutlook()
    {
        var graph = new ScriptedGraph(() => new HttpResponseMessage(HttpStatusCode.Accepted));
        var channels = ChannelFactory.Create(Outlook("a@qgs.eu"), new HttpClient(graph), outlookTokens: new FakeMailTokens());

        var result = await channels["email"].SendAsync(new Notification("T", "B", Severity.Info), default);

        Assert.True(result.Success);
        Assert.Equal(GraphMailSender.SendMailUri, Assert.Single(graph.Calls).Uri);
    }

    [Fact]
    public void Validator_AcceptsOutlookWithoutHostUserOrPassword()
    {
        Assert.Empty(ChannelSettingsValidator.Validate(Outlook()));
        Assert.Empty(ChannelSettingsValidator.Validate(Outlook("a@qgs.eu")));
    }

    [Fact]
    public void Validator_ChecksOutlookRecipientsTenantAndClient()
    {
        var settings = Outlook("rubbish");
        settings.Email.Outlook.TenantId = "non valido!";
        settings.Email.Outlook.ClientId = "not-a-guid";

        var errors = ChannelSettingsValidator.Validate(settings);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("destinatari"));
        Assert.Contains(errors, e => e.Contains("tenant"));
        Assert.Contains(errors, e => e.Contains("applicazione"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("e40359a3-6fe2-46f4-b190-a1f4f644834d")]
    [InlineData("azienda.onmicrosoft.com")]
    public void Validator_AcceptsGuidOrDomainTenants(string tenant)
    {
        var settings = Outlook();
        settings.Email.Outlook.TenantId = tenant;

        Assert.Empty(ChannelSettingsValidator.Validate(settings));
    }

    [Fact]
    public void ASettingsFileFromBeforeOutlook_StillMeansSmtp()
    {
        var old = """{"email":{"enabled":true,"host":"smtp.example.com","port":587,"from":"a@example.com","to":["b@example.com"]}}""";

        var settings = JsonSerializer.Deserialize<ChannelSettings>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(EmailProvider.Smtp, settings.Email.Provider);
        Assert.False(settings.Email.Outlook.SaveToSentItems);
        Assert.Empty(ChannelSettingsValidator.Validate(settings));
    }
}

public sealed class OutlookRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lhd-outlook-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ChannelStore SavedOutlook()
    {
        var store = new ChannelStore(Path.Combine(_directory, "channels.json"), NullLogger.Instance);
        store.Save(new ChannelSettings { Email = { Enabled = true, Provider = EmailProvider.Outlook } });
        return store;
    }

    [Fact]
    public void SavingOutlookSettings_NeedsNoSecret_AndComesBackUnchanged()
    {
        var store = SavedOutlook();

        var shown = ChannelStore.Masked(store.Current).Email;

        Assert.Equal(EmailProvider.Outlook, shown.Provider);
        Assert.True(string.IsNullOrEmpty(shown.Password));
    }

    [Fact]
    public void WhileNobodyIsSignedIn_DeliveryIsHeldInsteadOfFailing()
    {
        var tokens = new FakeMailTokens { Account = null };
        var registry = new ChannelRegistry(SavedOutlook(), new HttpClient(), outlookTokens: tokens);

        Assert.True(registry.IsHeldForSignIn());
        Assert.Null(registry.GetForDelivery("email"));
        Assert.NotNull(registry.Get("email")); // a test from the app still runs and reports the missing sign-in
    }

    [Fact]
    public void OnceSignedIn_DeliveryGoesAhead()
    {
        var registry = new ChannelRegistry(SavedOutlook(), new HttpClient(), outlookTokens: new FakeMailTokens());

        Assert.False(registry.IsHeldForSignIn());
        Assert.NotNull(registry.GetForDelivery("email"));
    }

    [Fact]
    public void SmtpMail_IsNeverHeldForASignIn()
    {
        var store = new ChannelStore(Path.Combine(_directory, "channels.json"), NullLogger.Instance);
        store.Save(new ChannelSettings { Email = { Enabled = true, Host = "smtp.example.com", From = "a@example.com", To = ["b@example.com"] } });
        var registry = new ChannelRegistry(store, new HttpClient(), outlookTokens: new FakeMailTokens { Account = null });

        Assert.False(registry.IsHeldForSignIn());
        Assert.NotNull(registry.GetForDelivery("email"));
    }
}

public class OutlookSessionTests
{
    private static ChannelSettings Outlook() => new() { Email = { Enabled = true, Provider = EmailProvider.Outlook } };

    [Fact]
    public async Task TheForm_ShowsTheProviderAndTheSignedInAccount()
    {
        var api = new FakeApi { Settings = Outlook(), MailAccount = "mario@qgs.eu" };
        var vm = new ChannelsViewModel(() => api);

        await vm.LoadAsync();

        Assert.True(vm.Email!.IsOutlook);
        Assert.False(vm.Email.IsSmtp);
        Assert.Equal("mario@qgs.eu", vm.Email.Session.Account);
        Assert.True(vm.Email.Session.IsSignedIn);
        Assert.False(vm.Email.Session.ShowSignIn);
    }

    [Fact]
    public async Task ChoosingOutlook_IsSavedAsTheProvider_WithTheOptions()
    {
        var api = new FakeApi();
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();

        vm.Email!.Provider = EmailEditor.Providers.First(p => p.Value == nameof(EmailProvider.Outlook));
        vm.Email.TenantId = " azienda.onmicrosoft.com ";
        vm.Email.SaveToSentItems = true;
        Assert.True(vm.Email.IsOutlook);
        await vm.SaveCommand.ExecuteAsync(null);

        var saved = api.Saved!.Email;
        Assert.Equal(EmailProvider.Outlook, saved.Provider);
        Assert.Equal("azienda.onmicrosoft.com", saved.Outlook.TenantId);
        Assert.True(saved.Outlook.SaveToSentItems);
    }

    [Fact]
    public async Task SigningIn_ShowsTheCode_OpensThePage_AndEndsConnected()
    {
        var api = new FakeApi { Settings = Outlook() };
        var opened = new List<Uri>();
        var vm = new ChannelsViewModel(() => api, _ => Task.CompletedTask, opened.Add);
        await vm.LoadAsync();
        Assert.True(vm.Email!.Session.ShowSignIn);

        await vm.Email.Session.SignInCommand.ExecuteAsync(null);

        Assert.Equal(1, api.MailLogins);
        Assert.Equal("https://microsoft.com/devicelogin", Assert.Single(opened).ToString());
        Assert.Equal("mario@qgs.eu", vm.Email.Session.Account);
        Assert.False(vm.Email.Session.LoginPending);
        Assert.Contains("Collegato", vm.Email.Session.Message);
        Assert.False(vm.IsDirty); // the sign-in is not an edit of the settings
    }

    [Fact]
    public async Task SigningIn_WithUnsavedChanges_AsksToSaveFirst()
    {
        var api = new FakeApi { Settings = Outlook() };
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();
        vm.Email!.TenantId = "azienda.onmicrosoft.com";

        await vm.Email.Session.SignInCommand.ExecuteAsync(null);

        Assert.Equal(0, api.MailLogins);
        Assert.Contains("Salva prima", vm.Email.Session.Message);
    }

    [Fact]
    public async Task SigningOut_ForgetsTheAccount()
    {
        var api = new FakeApi { Settings = Outlook(), MailAccount = "mario@qgs.eu" };
        var vm = new ChannelsViewModel(() => api);
        await vm.LoadAsync();

        await vm.Email!.Session.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, api.MailSignOuts);
        Assert.False(vm.Email.Session.IsSignedIn);
        Assert.True(vm.Email.Session.ShowSignIn);
    }
}
