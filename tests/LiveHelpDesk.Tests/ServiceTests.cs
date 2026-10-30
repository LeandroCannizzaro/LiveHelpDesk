using System.Security.AccessControl;
using System.Security.Principal;
using LiveHelpDesk.Dynamics;
using LiveHelpDesk.Ipc;
using LiveHelpDesk.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveHelpDesk.Tests;

internal sealed class FakeProfile(string sid) : IProfile
{
    private readonly FakeApi _api = new();

    public string Sid { get; } = sid;

    public bool Disposed { get; private set; }

    public bool Running { get; private set; }

    public event EventHandler<ChangedEventArgs>? Changed
    {
        add => _api.Changed += value;
        remove => _api.Changed -= value;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Running = true;
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            Running = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public Task<ServiceStatus> GetStatusAsync() => _api.GetStatusAsync();

    public Task<LoginChallenge> StartLoginAsync() => _api.StartLoginAsync();

    public Task<IReadOnlyList<EventInfo>> GetRecentEventsAsync(int count) => _api.GetRecentEventsAsync(count);

    public Task AcknowledgeAsync(string eventKey) => _api.AcknowledgeAsync(eventKey);

    public Task<IReadOnlyList<NotificationInfo>> ClaimNotificationsAsync(IReadOnlyList<string> channels) => _api.ClaimNotificationsAsync(channels);

    public Task<DryRunResult> DryRunRulesAsync() => _api.DryRunRulesAsync();
}

public class ProfileManagerTests
{
    private readonly List<FakeProfile> _created = [];

    private ProfileManager Manager(Func<string, Task>? beforeCreate = null) =>
        new(
            async sid =>
            {
                if (beforeCreate is not null)
                {
                    await beforeCreate(sid);
                }

                var profile = new FakeProfile(sid);
                lock (_created)
                {
                    _created.Add(profile);
                }

                return profile;
            },
            NullLogger.Instance);

    [Fact]
    public async Task TheSameUser_AlwaysGetsTheSameMonitor()
    {
        await using var manager = Manager();

        var first = await manager.GetOrStartAsync("S-1-5-21-1", CancellationToken.None);
        var second = await manager.GetOrStartAsync("S-1-5-21-1", CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(_created);
    }

    [Fact]
    public async Task DifferentUsers_GetDifferentMonitors_ThatNeverMeet()
    {
        await using var manager = Manager();

        var ann = await manager.GetOrStartAsync("S-1-5-21-1", CancellationToken.None);
        var bob = await manager.GetOrStartAsync("S-1-5-21-2", CancellationToken.None);

        Assert.NotSame(ann, bob);
        Assert.Equal(["S-1-5-21-1", "S-1-5-21-2"], manager.Sids.Order().ToArray());
    }

    [Fact]
    public async Task TwoSimultaneousFirstConnections_StartOneMonitor()
    {
        await using var manager = Manager(_ => Task.Delay(100));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => manager.GetOrStartAsync("S-1-5-21-1", CancellationToken.None)));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Single(_created);
    }

    [Fact]
    public async Task AMonitorThatFailsToStart_CanBeTriedAgainOnTheNextConnection()
    {
        var attempts = 0;
        await using var manager = Manager(_ => ++attempts == 1 ? throw new IOException("disk") : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => manager.GetOrStartAsync("S-1-5-21-1", CancellationToken.None));
        var profile = await manager.GetOrStartAsync("S-1-5-21-1", CancellationToken.None);

        Assert.NotNull(profile);
    }

    [Fact]
    public async Task StartingExistingProfiles_SkipsTheOnesThatFail()
    {
        await using var manager = Manager(sid => sid.EndsWith("bad") ? throw new IOException() : Task.CompletedTask);

        await manager.StartExistingAsync(["S-1-5-21-1", "S-1-5-21-bad", "S-1-5-21-2"]);

        Assert.Equal(2, _created.Count);
    }

    [Fact]
    public async Task Disposing_StopsEveryMonitorAndReleasesIt()
    {
        var manager = Manager();
        await manager.GetOrStartAsync("S-1-5-21-1", CancellationToken.None);
        await manager.GetOrStartAsync("S-1-5-21-2", CancellationToken.None);
        await Task.Delay(100); // let the loops start

        await manager.DisposeAsync();

        Assert.All(_created, p =>
        {
            Assert.False(p.Running);
            Assert.True(p.Disposed);
        });
    }
}

public class LoginCoordinatorTests
{
    private static DeviceCodeInfo Code(string user = "ABC123") =>
        new(user, "https://microsoft.com/devicelogin", T.At(15), $"Apri la pagina e inserisci {user}");

    [Fact]
    public async Task StartAsync_ReturnsTheCode_AsSoonAsItIsKnown_AndFinishesInTheBackground()
    {
        var finish = new TaskCompletionSource<string>();
        var signedIn = new TaskCompletionSource<string>();
        var coordinator = new LoginCoordinator(
            async (show, _) =>
            {
                await show(Code());
                return await finish.Task;
            },
            user =>
            {
                signedIn.SetResult(user);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            CancellationToken.None);

        var challenge = await coordinator.StartAsync();

        Assert.Equal("ABC123", challenge.UserCode);
        Assert.True(coordinator.InProgress);

        finish.SetResult("mario@qgs.eu");
        Assert.Equal("mario@qgs.eu", await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AskingAgainWhileWaiting_ReturnsTheSameCode_AndStartsNoSecondLogin()
    {
        var finish = new TaskCompletionSource<string>();
        var started = 0;
        var coordinator = new LoginCoordinator(
            async (show, _) =>
            {
                Interlocked.Increment(ref started);
                await show(Code());
                return await finish.Task;
            },
            _ => Task.CompletedTask,
            NullLogger.Instance,
            CancellationToken.None);

        var first = await coordinator.StartAsync();
        var second = await coordinator.StartAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, started);
        finish.SetResult("x");
    }

    [Fact]
    public async Task AFailedLogin_IsRemembered_AndAnotherCanBeStarted()
    {
        var attempt = 0;
        var coordinator = new LoginCoordinator(
            async (show, _) =>
            {
                await show(Code($"CODE{++attempt}"));
                throw new InvalidOperationException("l'utente ha annullato");
            },
            _ => Task.CompletedTask,
            NullLogger.Instance,
            CancellationToken.None);

        await coordinator.StartAsync();
        await Eventually(() => !coordinator.InProgress);

        Assert.Equal("l'utente ha annullato", coordinator.LastError);
        Assert.Equal("CODE2", (await coordinator.StartAsync()).UserCode);
    }

    [Fact]
    public async Task IfNoCodeEverComes_TheErrorIsReportedToTheCaller()
    {
        var coordinator = new LoginCoordinator(
            (_, _) => throw new InvalidOperationException("rete assente"),
            _ => Task.CompletedTask,
            NullLogger.Instance,
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync());

        Assert.Equal("rete assente", ex.Message);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}

public sealed class ProfileSecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lhd-acl-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            // The ACL we set is the only thing that matters here; we are the owner, so we can always clean up.
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void OnlySystemAdministratorsAndTheServiceAccount_CanReachAProfile()
    {
        ProfileSecurity.EnsureProtected(_directory);

        var who = ProfileSecurity.WhoCanAccess(_directory);

        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), who);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), who);
        Assert.Contains(WindowsIdentity.GetCurrent().User!, who);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), who);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), who);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), who);
    }

    [Fact]
    public void FilesCreatedInside_InheritTheRestriction()
    {
        ProfileSecurity.EnsureProtected(_directory);
        var file = Path.Combine(_directory, "msal-cache.bin");
        File.WriteAllText(file, "secret");

        var rules = new FileInfo(file).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToList();

        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), rules);
        Assert.Contains(WindowsIdentity.GetCurrent().User!, rules);
    }

    [Fact]
    public void ProtectingTwice_ChangesNothing()
    {
        ProfileSecurity.EnsureProtected(_directory);
        var before = ProfileSecurity.WhoCanAccess(_directory);

        ProfileSecurity.EnsureProtected(_directory);

        Assert.Equal(before.Select(s => s.Value).Order(), ProfileSecurity.WhoCanAccess(_directory).Select(s => s.Value).Order());
    }
}

public class ServiceInstallerTests
{
    private const string Exe = @"C:\Program Files\LiveHelpDesk\LiveHelpDesk.Service.exe";

    [Fact]
    public void Install_CreatesAnAutoStartingService_UnderAVirtualAccount()
    {
        var create = ServiceInstaller.InstallCommands(Exe)[0];

        Assert.Equal("create", create[0]);
        Assert.Equal("LiveHelpDesk", create[1]);
        Assert.Contains($"\"{Exe}\" run", create);
        Assert.Equal("delayed-auto", create[Array.IndexOf(create, "start=") + 1]);
        Assert.Equal("NT SERVICE\\LiveHelpDesk", create[Array.IndexOf(create, "obj=") + 1]);
    }

    [Fact]
    public void Install_RestartsTheServiceAfterACrash_AndAlsoAfterAnErrorExit()
    {
        var commands = ServiceInstaller.InstallCommands(Exe);

        var failure = Assert.Single(commands, c => c[0] == "failure");
        Assert.Contains("restart/5000/restart/30000/restart/60000", failure);
        Assert.Contains(commands, c => c[0] == "failureflag" && c[2] == "1");
    }

    [Fact]
    public void Uninstall_StopsTheServiceBeforeDeletingIt() =>
        Assert.Equal(["stop", "delete"], ServiceInstaller.UninstallCommands().Select(c => c[0]).ToArray());

    [Fact]
    public async Task WithoutAdministratorRights_NothingIsRun_AndTheUserIsToldWhy()
    {
        if (ServiceInstaller.IsElevated())
        {
            return; // elevated runners (CI) must never really run sc.exe from a test
        }

        var output = new List<string>();

        var code = await ServiceInstaller.RunAsync(ServiceInstaller.InstallCommands(Exe), output.Add);

        Assert.Equal(5, code);
        Assert.Contains("amministratore", Assert.Single(output));
    }
}
