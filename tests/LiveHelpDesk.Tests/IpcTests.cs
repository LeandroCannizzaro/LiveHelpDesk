using System.Security.Principal;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Ipc;
using LiveHelpDesk.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveHelpDesk.Tests;

internal sealed class FakeApi : ILiveHelpDeskApi
{
    public ServiceStatus Status { get; set; } =
        new(IpcProtocol.Version, "1.2.3", ProfileState.Running, "mario@qgs.eu", T.At(0), T.At(5), null, 446, 2);

    public List<string> Acknowledged { get; } = [];

    public List<string[]> Claims { get; } = [];

    public event EventHandler<ChangedEventArgs>? Changed;

    public void Raise(ChangeKind kind) => Changed?.Invoke(this, new ChangedEventArgs(kind));

    public Task<ServiceStatus> GetStatusAsync() => Task.FromResult(Status);

    public Task<LoginChallenge> StartLoginAsync() =>
        Task.FromResult(new LoginChallenge("ABC123", "https://microsoft.com/devicelogin", T.At(15), "Apri la pagina e inserisci ABC123"));

    public Task<IReadOnlyList<EventInfo>> GetRecentEventsAsync(int count) =>
        Task.FromResult<IReadOnlyList<EventInfo>>([new EventInfo("k1", "TicketCreated", null, "261000001", "Errore impegno", T.At(1), false)]);

    public Task AcknowledgeAsync(string eventKey)
    {
        Acknowledged.Add(eventKey);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NotificationInfo>> ClaimNotificationsAsync(IReadOnlyList<string> channels)
    {
        Claims.Add([.. channels]);
        var items = new[] { "toast", "overlay" }
            .Where(c => channels.Contains(c))
            .Select((c, i) => new NotificationInfo(7 + i, "k1", c, "Nuovo ticket #261000001", "corpo", Severity.Critical, new Uri("https://crm.example/t/1")));
        return Task.FromResult<IReadOnlyList<NotificationInfo>>([.. items]);
    }

    public ChannelSettings Settings { get; set; } = new()
    {
        Email = { Enabled = true, Host = "smtp.example.com", From = "a@example.com", To = ["b@example.com"], Password = "********" },
        Schedules =
        {
            ["sms"] = new ChannelSchedule
            {
                AlwaysAllowFrom = Severity.Critical,
                Windows = [new ChannelWindow { Days = [DayOfWeek.Monday], From = new TimeOnly(20, 0), To = new TimeOnly(8, 0) }],
            },
        },
    };

    public ChannelSettings? Saved { get; private set; }

    public List<string> Tested { get; } = [];

    public Task<ChannelSettings> GetChannelSettingsAsync() => Task.FromResult(Settings);

    public Task SaveChannelSettingsAsync(ChannelSettings settings)
    {
        Saved = settings;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChannelInfo>> GetChannelStatusAsync() =>
        Task.FromResult<IReadOnlyList<ChannelInfo>>([new ChannelInfo("email", true, "Closed", T.At(1), null, null, 3, 0), new ChannelInfo("sms", false, "Closed", null, null, null, 0, 0)]);

    public Task<ChannelTestResult> TestChannelAsync(string channelId)
    {
        Tested.Add(channelId);
        return Task.FromResult(channelId == "email" ? new ChannelTestResult(true, null) : new ChannelTestResult(false, "canale non attivo"));
    }

    public Task<DryRunResult> DryRunRulesAsync() =>
        Task.FromResult(new DryRunResult(3, [new RuleHitCount("new-tickets", "Nuovi ticket", 2)], [new DryRunTicket("261000001", "Errore", "new-tickets")]));
}

/// <summary>A real named pipe with a real JSON-RPC server on it, under a name nobody else uses.</summary>
internal sealed class PipeHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _running;

    public PipeHarness(Func<string, ILiveHelpDeskApi>? resolve = null, string? name = null)
    {
        Name = name ?? $"LiveHelpDeskTest-{Guid.NewGuid():N}";
        var server = new PipeServer(
            Name,
            (sid, _) =>
            {
                lock (Sids)
                {
                    Sids.Add(sid);
                }

                return Task.FromResult(resolve?.Invoke(sid) ?? Api);
            },
            NullLogger.Instance);
        _running = server.RunAsync(_stop.Token);
    }

    public string Name { get; }

    public FakeApi Api { get; } = new();

    public List<string> Sids { get; } = [];

    public Task<IpcConnection> ConnectAsync() => IpcClient.ConnectAsync(Name, TimeSpan.FromSeconds(5), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        _stop.Dispose();
    }
}

public class IpcTests
{
    [Fact]
    public async Task ACall_TravelsThePipe_AndComesBackWithItsTypes()
    {
        await using var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();

        var status = await connection.Api.GetStatusAsync();
        var login = await connection.Api.StartLoginAsync();
        var claimed = await connection.Api.ClaimNotificationsAsync(["toast", "overlay"]);
        var dryRun = await connection.Api.DryRunRulesAsync();

        Assert.Equal(ProfileState.Running, status.State);
        Assert.Equal(446, status.ActiveTickets);
        Assert.Equal(T.At(5), status.LastPollAt);
        Assert.Equal("ABC123", login.UserCode);
        Assert.Equal(T.At(15), login.ExpiresAt);
        Assert.Equal(["toast", "overlay"], claimed.Select(c => c.Channel).ToArray());
        Assert.All(claimed, note => Assert.Equal(Severity.Critical, note.Severity));
        Assert.Equal(new Uri("https://crm.example/t/1"), claimed[0].Url);
        Assert.Equal(["toast", "overlay"], harness.Api.Claims[0]);
        Assert.Equal(2, Assert.Single(dryRun.Rules).Matches);
    }

    [Fact]
    public async Task Acknowledging_ReachesTheService()
    {
        await using var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();

        await connection.Api.AcknowledgeAsync("k1");

        Assert.Equal(["k1"], harness.Api.Acknowledged);
    }

    [Fact]
    public async Task TheService_KnowsWhoIsCalling_FromTheirWindowsIdentity()
    {
        await using var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();
        await connection.Api.GetStatusAsync();

        var me = WindowsIdentity.GetCurrent().User!.Value;
        Assert.Equal([me], harness.Sids);
    }

    [Fact]
    public async Task EachCaller_GetsTheProfileTheResolverGivesForTheirIdentity()
    {
        var mine = new FakeApi { Status = new FakeApi().Status with { UserName = "io" } };
        await using var harness = new PipeHarness(_ => mine);
        await using var connection = await harness.ConnectAsync();

        Assert.Equal("io", (await connection.Api.GetStatusAsync()).UserName);
    }

    [Fact]
    public async Task ChangeNotices_ArePushedToTheConnectedApp()
    {
        await using var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();
        await connection.Api.GetStatusAsync(); // makes sure the server side is attached before raising

        var received = new TaskCompletionSource<ChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Api.Changed += (_, e) => received.TrySetResult(e.Kind);

        harness.Api.Raise(ChangeKind.Notifications);

        Assert.Equal(ChangeKind.Notifications, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SeveralAppsCanBeConnectedAtOnce()
    {
        await using var harness = new PipeHarness();
        await using var first = await harness.ConnectAsync();
        await using var second = await harness.ConnectAsync();

        Assert.Equal(446, (await first.Api.GetStatusAsync()).ActiveTickets);
        Assert.Equal(446, (await second.Api.GetStatusAsync()).ActiveTickets);
    }

    [Fact]
    public async Task WhenTheServiceStops_TheConnectionEnds()
    {
        var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();
        await connection.Api.GetStatusAsync();

        await harness.DisposeAsync();

        await connection.Disconnected.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectingToAServiceThatIsNotThere_AnswersNullWithinTheTimeout()
    {
        var connection = await IpcClient.TryConnectAsync($"LiveHelpDeskTest-{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(connection);
    }

    [Fact]
    public async Task ASecondServerOnTheSameName_IsRefused_SoNothingCanSquatOnIt()
    {
        await using var harness = new PipeHarness();

        Assert.ThrowsAny<Exception>(() => PipeServer.CreatePipe(harness.Name, exclusive: true));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AClientSpeakingAnotherProtocolVersion_IsDisconnected()
    {
        await using var harness = new PipeHarness();
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", harness.Name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        await pipe.WriteAsync(new[] { (byte)(IpcProtocol.Version + 1) });
        var read = await pipe.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, read); // the server hung up without answering
        Assert.Empty(harness.Sids);
    }

    [Fact]
    public async Task TheCommandLine_UsesTheSameChannelAsTheApp()
    {
        await using var harness = new PipeHarness();

        var status = await Cli.RunAsync("status", (_, timeout) => IpcClient.TryConnectAsync(harness.Name, timeout, CancellationToken.None));
        var dryRun = await Cli.RunAsync("dry-run", (_, timeout) => IpcClient.TryConnectAsync(harness.Name, timeout, CancellationToken.None));
        var absent = await Cli.RunAsync("status", (_, _) => Task.FromResult<IpcConnection?>(null));

        Assert.Equal(0, status);
        Assert.Equal(0, dryRun);
        Assert.Equal(3, absent);
    }

    [Fact]
    public async Task ChannelSettings_CrossThePipe_WithSchedulesTimesAndEnums()
    {
        await using var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();

        var settings = await connection.Api.GetChannelSettingsAsync();
        await connection.Api.SaveChannelSettingsAsync(settings);

        Assert.True(settings.Email.Enabled);
        Assert.Equal("********", settings.Email.Password);
        var window = Assert.Single(settings.Schedules["sms"].Windows);
        Assert.Equal(new TimeOnly(20, 0), window.From);
        Assert.Equal([DayOfWeek.Monday], window.Days);
        Assert.Equal(Severity.Critical, settings.Schedules["sms"].AlwaysAllowFrom);
        Assert.Equal(new TimeOnly(8, 0), harness.Api.Saved!.Schedules["sms"].Windows[0].To);
    }

    [Fact]
    public async Task ChannelStatusAndTests_TravelThePipe()
    {
        await using var harness = new PipeHarness();
        await using var connection = await harness.ConnectAsync();

        var status = await connection.Api.GetChannelStatusAsync();
        var ok = await connection.Api.TestChannelAsync("email");
        var bad = await connection.Api.TestChannelAsync("sms");

        Assert.Equal(["email", "sms"], status.Select(c => c.Id).ToArray());
        Assert.Equal(3, status[0].SentLastHour);
        Assert.True(ok.Ok);
        Assert.False(bad.Ok);
        Assert.Equal("canale non attivo", bad.Error);
    }

    [Fact]
    public async Task TheChannelCommands_UseThePipe_AndReportTheOutcomeInTheExitCode()
    {
        await using var harness = new PipeHarness();
        Task<IpcConnection?> Connect(string _, TimeSpan timeout) => IpcClient.TryConnectAsync(harness.Name, timeout, CancellationToken.None);

        Assert.Equal(0, await Cli.RunAsync("channels", Connect));
        Assert.Equal(0, await Cli.RunAsync("test", Connect, "email"));
        Assert.Equal(6, await Cli.RunAsync("test", Connect, "sms"));
        Assert.Equal(1, await Cli.RunAsync("test", Connect, null));
        Assert.Equal(0, await Cli.RunAsync("channels-template", (_, _) => Task.FromResult<IpcConnection?>(null)));
    }

    [Fact]
    public async Task ImportingSettings_SendsThemToTheService_AndAMissingFileIsRefused()
    {
        await using var harness = new PipeHarness();
        Task<IpcConnection?> Connect(string _, TimeSpan timeout) => IpcClient.TryConnectAsync(harness.Name, timeout, CancellationToken.None);
        var file = Path.Combine(Path.GetTempPath(), $"lhd-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """{ "email": { "enabled": true, "host": "smtp.example.com", "from": "a@example.com", "to": ["b@example.com"], "security": "SslOnConnect" } }""");
        try
        {
            Assert.Equal(0, await Cli.RunAsync("channels-import", Connect, file));
            Assert.Equal(1, await Cli.RunAsync("channels-import", Connect, file + ".missing"));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal(EmailSecurity.SslOnConnect, harness.Api.Saved!.Email.Security);
        Assert.Equal("smtp.example.com", harness.Api.Saved.Email.Host);
    }

    [Fact]
    public async Task LoggingIn_WhenAlreadySignedIn_DoesNotStartAnotherLogin()
    {
        await using var harness = new PipeHarness();

        var code = await Cli.RunAsync("login", (_, timeout) => IpcClient.TryConnectAsync(harness.Name, timeout, CancellationToken.None));

        Assert.Equal(0, code);
    }
}
