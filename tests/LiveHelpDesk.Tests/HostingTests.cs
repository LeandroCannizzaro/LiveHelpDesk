using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.Ipc;
using Xunit;

namespace LiveHelpDesk.Tests;

public sealed class HostingTests
{
    private sealed class FakeProcess(int id = 100) : IMonitoredProcess
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; } = id;

        public Task<int> Exited => _exited.Task;

        public bool Killed { get; private set; }

        public void End(int code = 0) => _exited.TrySetResult(code);

        public void Kill()
        {
            Killed = true;
            End(-1);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Launcher
    {
        public List<FakeProcess> Started { get; } = [];

        public bool EndsAtOnce { get; set; }

        public bool HonoursStopSignal { get; set; } = true;

        public int SignalsRaised { get; private set; }

        public FakeProcess Last => Started[^1];

        public IMonitoredProcess Start()
        {
            var process = new FakeProcess(100 + Started.Count);
            Started.Add(process);
            if (EndsAtOnce)
            {
                process.End(1);
            }

            return process;
        }

        public bool Raise()
        {
            SignalsRaised++;
            if (HonoursStopSignal && Started.Count > 0)
            {
                Last.End(0);
            }

            return Started.Count > 0;
        }
    }

    private static MonitorProcessHost HostFor(Launcher launcher, int stopAttempts = 3) =>
        new(launcher.Start, launcher.Raise, (_, _) => Task.CompletedTask, [TimeSpan.Zero, TimeSpan.Zero], stopAttempts: stopAttempts);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task Start_launches_the_process_and_reports_it_running()
    {
        var launcher = new Launcher();
        var host = HostFor(launcher);

        await host.StartAsync();

        Assert.Equal(MonitorProcessState.Running, host.State);
        Assert.Equal(100, host.ProcessId);
        Assert.True(host.IsActive);

        await host.StopAsync();
    }

    [Fact]
    public async Task Start_twice_does_not_launch_a_second_process()
    {
        var launcher = new Launcher();
        var host = HostFor(launcher);

        await host.StartAsync();
        await host.StartAsync();

        Assert.Single(launcher.Started);
        await host.StopAsync();
    }

    [Fact]
    public async Task Stop_asks_politely_and_does_not_kill_a_process_that_answers()
    {
        var launcher = new Launcher();
        var host = HostFor(launcher);
        await host.StartAsync();

        await host.StopAsync();

        Assert.Equal(1, launcher.SignalsRaised);
        Assert.False(launcher.Last.Killed);
        Assert.Equal(MonitorProcessState.Stopped, host.State);
        Assert.False(host.IsActive);
    }

    [Fact]
    public async Task Stop_kills_a_process_that_ignores_the_signal()
    {
        var launcher = new Launcher { HonoursStopSignal = false };
        var host = HostFor(launcher, stopAttempts: 2);
        await host.StartAsync();

        await host.StopAsync();

        Assert.Equal(2, launcher.SignalsRaised);
        Assert.True(launcher.Last.Killed);
        Assert.Equal(MonitorProcessState.Stopped, host.State);
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task A_process_that_ends_by_itself_is_started_again()
    {
        var launcher = new Launcher();
        var host = HostFor(launcher);
        await host.StartAsync();

        launcher.Started[0].End(3);

        await Until(() => launcher.Started.Count == 2 && host.State == MonitorProcessState.Running);
        Assert.Equal(3, host.LastExitCode);
        await host.StopAsync();
        Assert.Equal(2, launcher.Started.Count);
    }

    [Fact]
    public async Task A_process_that_keeps_dying_ends_up_failed_instead_of_looping_forever()
    {
        var launcher = new Launcher { EndsAtOnce = true };
        var host = HostFor(launcher);

        await host.StartAsync();

        await Until(() => host.State == MonitorProcessState.Failed);
        Assert.Equal(3, launcher.Started.Count);
        Assert.False(host.IsActive);
        Assert.Contains("di seguito", host.LastError);
    }

    [Fact]
    public async Task A_launch_that_throws_is_reported_not_raised()
    {
        var host = new MonitorProcessHost(() => throw new FileNotFoundException("manca"), () => false);

        await host.StartAsync();

        Assert.Equal(MonitorProcessState.Failed, host.State);
        Assert.Equal("manca", host.LastError);
    }

    [Fact]
    public async Task After_failing_it_can_be_started_again()
    {
        var launcher = new Launcher { EndsAtOnce = true };
        var host = HostFor(launcher);
        await host.StartAsync();
        await Until(() => host.State == MonitorProcessState.Failed);

        launcher.EndsAtOnce = false;
        await host.StartAsync();

        Assert.Equal(MonitorProcessState.Running, host.State);
        await host.StopAsync();
    }

    [Fact]
    public void The_stop_signal_reaches_whoever_listens_and_nobody_otherwise()
    {
        var name = $@"Local\LiveHelpDesk.Test.{Guid.NewGuid():N}";

        Assert.False(StopSignal.Raise(name));

        using var listener = StopSignal.Create(name);
        Assert.False(listener.WaitOne(0));
        Assert.True(StopSignal.Raise(name));
        Assert.True(listener.WaitOne(1000));
    }

    // ---- the Windows service ----------------------------------------------------------------------------------

    private sealed class FakeRunner(int exitCode = 0, bool elevated = false) : IElevatedRunner
    {
        public bool IsElevated { get; } = elevated;

        public List<string> Calls { get; } = [];

        public Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add($"{executablePath} {string.Join(' ', arguments)}");
            return Task.FromResult(exitCode);
        }
    }

    [Theory]
    [InlineData(ServiceAction.Install, "install")]
    [InlineData(ServiceAction.Uninstall, "uninstall")]
    [InlineData(ServiceAction.Start, "start")]
    [InlineData(ServiceAction.Stop, "stop")]
    public async Task Each_action_runs_the_service_program_with_its_own_verb(ServiceAction action, string verb)
    {
        var runner = new FakeRunner();
        var manager = new WindowsServiceManager(() => @"C:\x\LiveHelpDesk.Service.exe", runner);

        var result = await manager.RunAsync(action);

        Assert.True(result.Ok);
        Assert.Equal([$@"C:\x\LiveHelpDesk.Service.exe {verb}"], runner.Calls);
    }

    [Fact]
    public async Task A_missing_service_program_is_a_clear_message_not_an_exception()
    {
        var runner = new FakeRunner();
        var manager = new WindowsServiceManager(() => null, runner);

        var result = await manager.RunAsync(ServiceAction.Install);

        Assert.False(result.Ok);
        Assert.Contains("LiveHelpDesk.Service.exe", result.Message);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(ServiceAction.Install, UacRunner.Declined, false, "annullata")]
    [InlineData(ServiceAction.Install, 5, false, "amministratore")]
    [InlineData(ServiceAction.Install, 1073, false, "già installato")]
    [InlineData(ServiceAction.Uninstall, 1060, true, "non era installato")]
    [InlineData(ServiceAction.Start, 1056, true, "già in esecuzione")]
    [InlineData(ServiceAction.Stop, 1062, true, "già fermo")]
    [InlineData(ServiceAction.Stop, 77, false, "77")]
    public void Exit_codes_become_messages_the_operator_can_act_on(ServiceAction action, int code, bool ok, string text)
    {
        var result = WindowsServiceManager.Explain(action, code);

        Assert.Equal(ok, result.Ok);
        Assert.Contains(text, result.Message);
    }

    private sealed class FakeServices(WindowsServiceState state, bool elevated = false) : IWindowsServiceManager
    {
        public bool IsElevated { get; } = elevated;

        public WindowsServiceState State { get; set; } = state;

        public List<ServiceAction> Actions { get; } = [];

        /// <summary>Whether the action really changes the service (an elevated run can succeed on paper and still change nothing).</summary>
        public bool Effective { get; set; } = true;

        public ServiceActionResult Result { get; set; } = new(true, "ok");

        public WindowsServiceState Query() => State;

        public Task<ServiceActionResult> RunAsync(ServiceAction action, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            if (Result.Ok && Effective)
            {
                State = action switch
                {
                    ServiceAction.Install or ServiceAction.Start => WindowsServiceState.Running,
                    ServiceAction.Uninstall => WindowsServiceState.NotInstalled,
                    _ => WindowsServiceState.Stopped,
                };
            }

            return Task.FromResult(Result);
        }
    }

    private static HostingViewModel Hosting(FakeServices services, Launcher launcher, bool autoStart = true, List<bool>? saved = null) =>
        new(services, HostFor(launcher), autoStart, value => saved?.Add(value), delay: _ => Task.CompletedTask, verifyAttempts: 2);

    [Fact]
    public async Task Without_a_service_start_and_stop_create_and_end_the_process()
    {
        var services = new FakeServices(WindowsServiceState.NotInstalled);
        var launcher = new Launcher();
        var hosting = Hosting(services, launcher);
        hosting.Refresh(LinkState.ServiceMissing);
        Assert.True(hosting.CanStart);
        Assert.False(hosting.CanStop);
        Assert.True(hosting.CanInstall);
        Assert.False(hosting.CanUninstall);

        await hosting.StartCommand.ExecuteAsync(null);

        Assert.Single(launcher.Started);
        Assert.Empty(services.Actions);
        Assert.True(hosting.CanStop);
        Assert.False(hosting.CanStart);
        Assert.Contains("PID", hosting.ProcessLine);

        await hosting.StopCommand.ExecuteAsync(null);

        Assert.Empty(services.Actions);
        Assert.True(hosting.CanStart);
        Assert.False(hosting.CanStop);
    }

    [Fact]
    public async Task With_the_service_installed_start_and_stop_go_to_windows_not_to_a_process()
    {
        var services = new FakeServices(WindowsServiceState.Stopped);
        var launcher = new Launcher();
        var hosting = Hosting(services, launcher);
        hosting.Refresh(LinkState.ServiceMissing);
        Assert.Equal("Avvia il servizio", hosting.StartLabel);
        Assert.False(hosting.CanInstall);
        Assert.True(hosting.CanUninstall);

        await hosting.StartCommand.ExecuteAsync(null);
        await hosting.StopCommand.ExecuteAsync(null);

        Assert.Equal([ServiceAction.Start, ServiceAction.Stop], services.Actions);
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task Installing_makes_room_by_ending_the_process_the_app_started()
    {
        var services = new FakeServices(WindowsServiceState.NotInstalled);
        var launcher = new Launcher();
        var hosting = Hosting(services, launcher);
        await hosting.StartCommand.ExecuteAsync(null);

        await hosting.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, launcher.SignalsRaised);
        Assert.Equal([ServiceAction.Install], services.Actions);
        Assert.True(hosting.ServiceInstalled);
        Assert.Equal("ok", hosting.Message);
    }

    [Fact]
    public async Task Uninstall_removes_only_the_service()
    {
        var services = new FakeServices(WindowsServiceState.Running);
        var hosting = Hosting(services, new Launcher());

        await hosting.UninstallCommand.ExecuteAsync(null);

        Assert.Equal([ServiceAction.Uninstall], services.Actions);
        Assert.False(hosting.ServiceInstalled);
        Assert.True(hosting.CanInstall);
    }

    [Fact]
    public async Task A_refused_prompt_leaves_things_as_they_were_and_says_so()
    {
        var services = new FakeServices(WindowsServiceState.NotInstalled) { Result = new(false, "Operazione annullata: servono i permessi di amministratore.") };
        var hosting = Hosting(services, new Launcher());

        await hosting.InstallCommand.ExecuteAsync(null);

        Assert.Contains("annullata", hosting.Message);
        Assert.False(hosting.ServiceInstalled);
        Assert.True(hosting.CanInstall);
    }

    [Fact]
    public async Task An_exit_code_of_zero_is_not_trusted_if_the_service_did_not_change()
    {
        var services = new FakeServices(WindowsServiceState.NotInstalled) { Effective = false };
        var hosting = Hosting(services, new Launcher());

        await hosting.InstallCommand.ExecuteAsync(null);

        Assert.Contains("non è cambiato", hosting.Message);
        Assert.False(hosting.ServiceInstalled);
    }

    [Fact]
    public async Task The_process_starts_by_itself_once_when_there_is_no_service_and_the_option_is_on()
    {
        var services = new FakeServices(WindowsServiceState.NotInstalled);
        var launcher = new Launcher();
        var hosting = Hosting(services, launcher);

        await hosting.AutoStartAsync(LinkState.Connecting);
        Assert.Empty(launcher.Started);

        await hosting.AutoStartAsync(LinkState.ServiceMissing);
        await hosting.AutoStartAsync(LinkState.ServiceMissing);

        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task The_process_does_not_start_by_itself_when_the_option_is_off_the_service_exists_or_the_user_stopped_it()
    {
        var off = new Launcher();
        await Hosting(new FakeServices(WindowsServiceState.NotInstalled), off, autoStart: false).AutoStartAsync(LinkState.ServiceMissing);
        Assert.Empty(off.Started);

        var withService = new Launcher();
        await Hosting(new FakeServices(WindowsServiceState.Stopped), withService).AutoStartAsync(LinkState.ServiceMissing);
        Assert.Empty(withService.Started);

        var stopped = new Launcher();
        var hosting = Hosting(new FakeServices(WindowsServiceState.NotInstalled), stopped);
        await hosting.StopCommand.ExecuteAsync(null);
        await hosting.AutoStartAsync(LinkState.ServiceMissing);
        Assert.Empty(stopped.Started);
    }

    [Fact]
    public async Task Nothing_is_started_when_a_monitor_is_already_answering()
    {
        var launcher = new Launcher();
        var hosting = Hosting(new FakeServices(WindowsServiceState.NotInstalled), launcher);
        hosting.Refresh(LinkState.Connected);

        await hosting.StartCommand.ExecuteAsync(null);

        Assert.Empty(launcher.Started);
        Assert.Contains("già in funzione", hosting.Message);
    }

    [Fact]
    public void Changing_the_option_is_saved()
    {
        var saved = new List<bool>();
        var hosting = Hosting(new FakeServices(WindowsServiceState.NotInstalled), new Launcher(), saved: saved);

        hosting.AutoStart = false;

        Assert.Equal([false], saved);
    }

    [Fact]
    public void The_service_program_is_found_next_to_the_app_or_not_at_all()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"lhd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(ServiceExecutable.Find(folder));

            var exe = Path.Combine(folder, ServiceExecutable.FileName);
            File.WriteAllText(exe, "");

            Assert.Equal(exe, ServiceExecutable.Find(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
