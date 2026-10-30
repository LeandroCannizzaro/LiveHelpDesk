using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Channels;
using Xunit;

namespace LiveHelpDesk.Tests;

internal sealed class RecordingEmailSender(Exception? failure = null) : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (failure is not null)
        {
            throw failure;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Just enough SMTP to receive one message from a real client: the protocol is what is being tested.</summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<string> Commands { get; } = [];

    public List<string> Messages { get; } = [];

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("220 localhost ESMTP ready");
        while (await reader.ReadLineAsync(_stop.Token) is { } line)
        {
            Commands.Add(line);
            var verb = line.Split(' ')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 8BITMIME");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } row && row != ".")
                    {
                        data.AppendLine(row);
                    }

                    Messages.Add(data.ToString());
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("250 ok");
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }
}

public class EmailChannelTests
{
    private static EmailSettings Settings() => new() { Enabled = true, Host = "smtp.example.com", From = "helpdesk@example.com", To = ["a@example.com", "b@example.com"] };

    [Fact]
    public async Task ACriticalNotification_BecomesAnUrgentMessageWithALinkToTheTicket()
    {
        var sender = new RecordingEmailSender();
        var channel = new EmailChannel(Settings(), sender);

        var result = await channel.SendAsync(
            new Notification("Ticket #1 senza risposta", "Errore impegno", Severity.Critical, new Uri("https://crm.example/t/1")), CancellationToken.None);

        Assert.True(result.Success);
        var message = Assert.Single(sender.Sent);
        Assert.Equal("[URGENTE] Ticket #1 senza risposta", message.Subject);
        Assert.Equal(["a@example.com", "b@example.com"], message.To);
        Assert.Contains("Errore impegno", message.TextBody);
        Assert.Contains("Apri il ticket: https://crm.example/t/1", message.TextBody);
    }

    [Fact]
    public async Task AnOrdinaryNotification_KeepsItsTitleAsTheSubject()
    {
        var sender = new RecordingEmailSender();

        await new EmailChannel(Settings(), sender).SendAsync(new Notification("Nuovo ticket #1", "corpo", Severity.Attention), CancellationToken.None);

        Assert.Equal("Nuovo ticket #1", sender.Sent[0].Subject);
    }

    [Fact]
    public async Task AnIncompleteConfiguration_FailsWithoutTryingToSend()
    {
        var sender = new RecordingEmailSender();
        var settings = Settings();
        settings.To.Clear();

        var result = await new EmailChannel(settings, sender).TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("incompleta", result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ATransportError_IsReportedAsAFailure_NotThrown()
    {
        var channel = new EmailChannel(Settings(), new RecordingEmailSender(new IOException("connessione chiusa")));

        var result = await channel.SendAsync(new Notification("t", "b", Severity.Info), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("connessione chiusa", result.Error);
    }

    [Fact]
    public async Task TheTestMessage_IsClearlyATest()
    {
        var sender = new RecordingEmailSender();

        var result = await new EmailChannel(Settings(), sender).TestAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("prova", sender.Sent[0].Subject);
    }

    [Fact]
    public async Task ARealSmtpConversation_DeliversTheMessage()
    {
        await using var server = new FakeSmtpServer();
        var settings = new EmailSettings
        {
            Enabled = true, Host = "127.0.0.1", Port = server.Port, Security = EmailSecurity.None,
            From = "helpdesk@example.com", To = ["a@example.com", "b@example.com"],
        };
        var channel = new EmailChannel(settings, new MailKitSender(settings));

        var result = await channel.SendAsync(
            new Notification("Ticket 261000001", "Errore impegno DB26", Severity.Critical, new Uri("https://crm.example/t/1")), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains(server.Commands, c => c.StartsWith("MAIL FROM:<helpdesk@example.com>", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, server.Commands.Count(c => c.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)));
        var message = Assert.Single(server.Messages);
        Assert.Contains("Subject: [URGENTE] Ticket 261000001", message);
        Assert.Contains("Errore impegno DB26", message);
        Assert.Contains("https://crm.example/t/1", message);
    }

    [Fact]
    public async Task AnUnreachableServer_IsAFailureWithAMessage()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var settings = new EmailSettings
        {
            Enabled = true, Host = "127.0.0.1", Port = port, Security = EmailSecurity.None, From = "a@example.com", To = ["b@example.com"],
        };

        var result = await new EmailChannel(settings, new MailKitSender(settings)).TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("non riuscito", result.Error);
    }
}

public class WebhookChannelTests
{
    private const string Secret = "https://prod-00.westeurope.logic.azure.com/workflows/abc123/triggers/manual/paths/invoke?sig=TOP-SECRET";

    private static WebhookSettings Settings(WebhookFormat format = WebhookFormat.TeamsCard) => new() { Enabled = true, Url = Secret, Format = format };

    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted);

    [Fact]
    public async Task ATeamsMessage_IsAnAdaptiveCard_WithTheTitleBodyAndAButton()
    {
        var handler = new FakeHandler((_, _) => Accepted());
        var channel = new WebhookChannel("teams", Settings(), new HttpClient(handler));

        var result = await channel.SendAsync(
            new Notification("Nuovo ticket #1", "Errore impegno", Severity.Critical, new Uri("https://crm.example/t/1")), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Secret, handler.Requests[0].Uri.ToString());
        var json = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal("message", (string?)json["type"]);
        var attachment = json["attachments"]![0]!;
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string?)attachment["contentType"]);
        var card = attachment["content"]!;
        Assert.Equal("AdaptiveCard", (string?)card["type"]);
        Assert.Equal("Nuovo ticket #1", (string?)card["body"]![0]!["text"]);
        Assert.Equal("Attention", (string?)card["body"]![0]!["color"]);
        Assert.Equal("Errore impegno", (string?)card["body"]![1]!["text"]);
        Assert.Equal("Action.OpenUrl", (string?)card["actions"]![0]!["type"]);
        Assert.Equal("https://crm.example/t/1", (string?)card["actions"]![0]!["url"]);
    }

    [Fact]
    public async Task ANotificationWithoutALink_HasNoButton()
    {
        var handler = new FakeHandler((_, _) => Accepted());

        await new WebhookChannel("teams", Settings(), new HttpClient(handler)).SendAsync(new Notification("t", "b", Severity.Info), CancellationToken.None);

        var card = JsonNode.Parse(handler.Bodies[0])!["attachments"]![0]!["content"]!;
        Assert.Null(card["actions"]);
        Assert.Equal("Default", (string?)card["body"]![0]!["color"]);
    }

    [Fact]
    public async Task ThePlainJsonFormat_CarriesTheFieldsForAnyReceiver()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));

        await new WebhookChannel("teams", Settings(WebhookFormat.Json), new HttpClient(handler))
            .SendAsync(new Notification("t", "b", Severity.Attention, new Uri("https://crm.example/t/1")), CancellationToken.None);

        var json = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal("t", (string?)json["title"]);
        Assert.Equal("Attention", (string?)json["severity"]);
        Assert.Equal("https://crm.example/t/1", (string?)json["url"]);
    }

    [Fact]
    public async Task ARejection_IsReported_WithoutRevealingTheAddress()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{ "error": "bad request" }""", HttpStatusCode.BadRequest));

        var result = await new WebhookChannel("teams", Settings(), new HttpClient(handler)).TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("400", result.Error);
        Assert.DoesNotContain("TOP-SECRET", result.Error);
        Assert.DoesNotContain("abc123", result.Error);
    }

    [Fact]
    public async Task ANetworkError_IsReported_WithoutRevealingTheAddress()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException($"failed to reach {Secret}"));

        var result = await new WebhookChannel("teams", Settings(), new HttpClient(handler)).TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("TOP-SECRET", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://insecure.example.com/hook")]
    public async Task ABadAddress_IsRefusedBeforeAnyRequest(string url)
    {
        var handler = new FakeHandler((_, _) => Accepted());
        var settings = Settings();
        settings.Url = url;

        var result = await new WebhookChannel("teams", settings, new HttpClient(handler)).TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }
}

public class TwilioChannelTests
{
    private static TwilioSettings Settings(params string[] to) => new()
    {
        Enabled = true, AccountSid = "ACd0c0ffee0123456789", AuthToken = "s3cret-token", From = "+390212345678",
        To = to.Length == 0 ? ["+393331234567"] : [.. to],
    };

    private static HttpResponseMessage Created() => FakeHandler.Json("""{ "sid": "SM123", "status": "queued" }""", HttpStatusCode.Created);

    private static System.Collections.Specialized.NameValueCollection Form(string body) => HttpUtility.ParseQueryString(body);

    [Fact]
    public async Task ATextMessage_IsPostedToTwilio_WithBasicAuthentication()
    {
        var handler = new FakeHandler((_, _) => Created());
        var channel = new TwilioChannel(TwilioKind.Sms, Settings(), new HttpClient(handler));

        var result = await channel.SendAsync(new Notification("Nuovo ticket #1", "#1 · Priorità Alta", Severity.Critical, new Uri("https://crm.example/t/1")), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/ACd0c0ffee0123456789/Messages.json", request.Uri.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ACd0c0ffee0123456789:s3cret-token")), request.Authorization);
        var form = Form(handler.Bodies[0]);
        Assert.Equal("+393331234567", form["To"]);
        Assert.Equal("+390212345678", form["From"]);
        Assert.Contains("Nuovo ticket #1", form["Body"]);
        Assert.Contains("https://crm.example/t/1", form["Body"]);
    }

    [Fact]
    public async Task EveryRecipient_GetsItsOwnMessage()
    {
        var handler = new FakeHandler((_, _) => Created());
        var channel = new TwilioChannel(TwilioKind.Sms, Settings("+393331111111", "+393332222222"), new HttpClient(handler));

        await channel.SendAsync(new Notification("t", "b", Severity.Info), CancellationToken.None);

        Assert.Equal(["+393331111111", "+393332222222"], handler.Bodies.Select(b => Form(b)["To"]!).ToArray());
    }

    [Fact]
    public async Task OneRecipientFailing_IsReported_WithoutShowingTheWholeNumber()
    {
        var handler = new FakeHandler((request, n) => n == 2
            ? FakeHandler.Json("""{ "code": 21211, "message": "Invalid 'To' Phone Number" }""", HttpStatusCode.BadRequest)
            : Created());
        var channel = new TwilioChannel(TwilioKind.Sms, Settings("+393331111111", "+393332222222"), new HttpClient(handler));

        var result = await channel.SendAsync(new Notification("t", "b", Severity.Info), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("21211", result.Error);
        Assert.Contains("…2222", result.Error);
        Assert.DoesNotContain("393332222222", result.Error);
        Assert.Equal(2, handler.Requests.Count); // the first recipient was still served
    }

    [Fact]
    public async Task ALongTextMessage_IsCutAtThreeHundredTwentyCharacters()
    {
        var handler = new FakeHandler((_, _) => Created());
        var channel = new TwilioChannel(TwilioKind.Sms, Settings(), new HttpClient(handler));

        await channel.SendAsync(new Notification("Titolo", new string('x', 900), Severity.Info), CancellationToken.None);

        var body = Form(handler.Bodies[0])["Body"]!;
        Assert.Equal(320, body.Length);
        Assert.EndsWith("…", body);
    }

    [Fact]
    public async Task WhatsApp_PrefixesTheNumbers()
    {
        var handler = new FakeHandler((_, _) => Created());
        var settings = Settings();
        settings.From = "+14155238886";
        var channel = new TwilioChannel(TwilioKind.WhatsApp, settings, new HttpClient(handler));

        await channel.SendAsync(new Notification("t", "b", Severity.Info), CancellationToken.None);

        var form = Form(handler.Bodies[0]);
        Assert.Equal("whatsapp:+393331234567", form["To"]);
        Assert.Equal("whatsapp:+14155238886", form["From"]);
        Assert.NotNull(form["Body"]);
        Assert.Null(form["ContentSid"]);
    }

    [Fact]
    public async Task WhatsApp_WithATemplate_SendsTheTemplateInsteadOfFreeText()
    {
        var handler = new FakeHandler((_, _) => Created());
        var settings = Settings();
        settings.ContentSid = "HXabc";
        var channel = new TwilioChannel(TwilioKind.WhatsApp, settings, new HttpClient(handler));

        await channel.SendAsync(new Notification("Nuovo ticket #1", "dettaglio", Severity.Info), CancellationToken.None);

        var form = Form(handler.Bodies[0]);
        Assert.Equal("HXabc", form["ContentSid"]);
        Assert.Null(form["Body"]);
        var variables = JsonNode.Parse(form["ContentVariables"]!)!;
        Assert.Equal("Nuovo ticket #1", (string?)variables["1"]);
        Assert.Equal("dettaglio", (string?)variables["2"]);
    }

    [Fact]
    public async Task TheIdsMatchTheKindOfMessage()
    {
        var http = new HttpClient(new FakeHandler((_, _) => Created()));

        Assert.Equal("sms", new TwilioChannel(TwilioKind.Sms, Settings(), http).Id);
        Assert.Equal("whatsapp", new TwilioChannel(TwilioKind.WhatsApp, Settings(), http).Id);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnIncompleteConfiguration_FailsWithoutAnyRequest()
    {
        var handler = new FakeHandler((_, _) => Created());
        var settings = Settings();
        settings.AuthToken = "";

        var result = await new TwilioChannel(TwilioKind.Sms, settings, new HttpClient(handler)).TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ANetworkError_IsReportedAsAFailure()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("no route"));

        var result = await new TwilioChannel(TwilioKind.Sms, Settings(), new HttpClient(handler)).TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("non raggiungibile", result.Error);
    }
}

public class ChannelFactoryAndValidatorTests
{
    private static ChannelSettings Valid() => new()
    {
        Email = { Enabled = true, Host = "smtp.example.com", From = "helpdesk@example.com", To = ["a@example.com"] },
        Teams = { Enabled = true, Url = "https://prod.logic.azure.com/hook" },
        Sms = { Enabled = true, AccountSid = "ACd0c0ffee0123456789", AuthToken = "t", From = "+390212345678", To = ["+393331234567"] },
        WhatsApp = { Enabled = true, AccountSid = "ACd0c0ffee0123456789", AuthToken = "t", From = "+14155238886", To = ["+393331234567"] },
    };

    [Fact]
    public void OnlyTheSwitchedOnChannels_Exist()
    {
        var settings = Valid();
        settings.Sms.Enabled = false;
        settings.WhatsApp.Enabled = false;

        var channels = ChannelFactory.Create(settings, new HttpClient(), new RecordingEmailSender());

        Assert.Equal(["email", "teams"], channels.Keys.Order().ToArray());
        Assert.Empty(ChannelFactory.Create(new ChannelSettings(), new HttpClient()));
    }

    [Fact]
    public void CompleteSettings_AreValid() =>
        Assert.Empty(ChannelSettingsValidator.Validate(Valid()));

    [Fact]
    public void ChannelsThatAreOff_AreNotChecked() =>
        Assert.Empty(ChannelSettingsValidator.Validate(new ChannelSettings { Email = { Host = "", From = "rubbish" } }));

    [Theory]
    [InlineData("email-host", "server SMTP")]
    [InlineData("email-from", "mittente")]
    [InlineData("email-to", "destinatario")]
    [InlineData("email-port", "porta")]
    [InlineData("email-user", "password")]
    [InlineData("teams-http", "https")]
    [InlineData("sms-sid", "Account SID")]
    [InlineData("sms-token", "Auth Token")]
    [InlineData("sms-from", "mittente")]
    [InlineData("sms-to", "destinatario")]
    [InlineData("whatsapp-to", "WhatsApp")]
    [InlineData("limit", "Limite")]
    public void EachMistake_IsExplained(string mistake, string expected)
    {
        var settings = Valid();
        switch (mistake)
        {
            case "email-host": settings.Email.Host = ""; break;
            case "email-from": settings.Email.From = "not an address"; break;
            case "email-to": settings.Email.To = ["fine@example.com", "broken"]; break;
            case "email-port": settings.Email.Port = 0; break;
            case "email-user": settings.Email.User = "me"; settings.Email.Password = ""; break;
            case "teams-http": settings.Teams.Url = "http://insecure.example.com"; break;
            case "sms-sid": settings.Sms.AccountSid = "XX"; break;
            case "sms-token": settings.Sms.AuthToken = ""; break;
            case "sms-from": settings.Sms.From = "0212345678"; break;
            case "sms-to": settings.Sms.To = ["333 1234567"]; break;
            case "whatsapp-to": settings.WhatsApp.To = []; break;
            case "limit": settings.MaxPerHour["sms"] = 0; break;
        }

        var errors = ChannelSettingsValidator.Validate(settings);

        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }
}
