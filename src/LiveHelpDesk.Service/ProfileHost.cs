using LiveHelpDesk.Core.Health;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Core.Sources;
using LiveHelpDesk.Core.State;
using LiveHelpDesk.Ipc;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>One user's monitor, running inside the service and answering that user's app.</summary>
public interface IProfile : ILiveHelpDeskApi, IAsyncDisposable
{
    /// <summary>Polls until <paramref name="cancellationToken"/> is cancelled.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

public sealed class ProfileHost : IProfile
{
    private static readonly string ServiceVersion = typeof(ProfileHost).Assembly.GetName().Version?.ToString(3) ?? "0";

    private readonly Pipeline _pipeline;
    private readonly ILogger _logger;
    private readonly SqliteStateStore _api; // a second connection: the poller's one is never touched from other threads
    private readonly object _apiGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly LoginCoordinator _login;

    private string? _userName;
    private int _failures;
    private string? _lastError;
    private bool _needsLogin;
    private DateTimeOffset? _lastPollAt;
    private ProfileState? _announced;

    public ProfileHost(Pipeline pipeline, ILogger logger)
    {
        _pipeline = pipeline;
        _logger = logger;
        _api = new SqliteStateStore(Path.Combine(pipeline.ProfileDirectory, "state.db"));
        _login = new LoginCoordinator(
            (show, ct) => pipeline.Tokens.LoginWithDeviceCodeAsync(show, ct), OnSignedInAsync, logger, _stopping.Token);
    }

    public event EventHandler<ChangedEventArgs>? Changed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var stop = linked.Token;
        _logger.LogInformation("Monitor started (every {Seconds}s)", _pipeline.Settings.PollInterval.TotalSeconds);

        while (!stop.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await _pipeline.EnsureMeAsync(stop);
                var result = await _pipeline.Engine.PollOnceAsync(stop);

                _failures = 0;
                _lastError = null;
                _needsLogin = false;
                _lastPollAt = DateTimeOffset.UtcNow;

                var now = DateTimeOffset.UtcNow;
                var recovered = _pipeline.Health.Success(now);
                if (recovered.Count > 0)
                {
                    _pipeline.Engine.Publish(recovered, now);
                }

                if (result.Events.Count > 0 || result.BaselineTaken)
                {
                    _logger.LogInformation(
                        "Poll done: {Events} events, {Queued} notifications queued{Baseline}",
                        result.Events.Count, result.Queued, result.BaselineTaken ? " (baseline)" : "");
                    Raise(ChangeKind.Events);
                }

                if (result.Queued > 0)
                {
                    Raise(ChangeKind.Notifications);
                }

                delay = PollingSchedule.AfterSuccess(_pipeline.Settings.PollInterval, Random.Shared.NextDouble());
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (TicketSourceException ex)
            {
                delay = Failed(ex);
            }
            catch (Exception ex)
            {
                // A bug or a full disk must not kill the monitor: report it like any other failure and carry on.
                _logger.LogError(ex, "Unexpected error while polling");
                delay = Failed(new TicketSourceException(SourceFailure.Error, ex.Message, inner: ex));
            }

            await AnnounceIfStateChangedAsync();
            await WaitAsync(delay, stop);
        }

        _logger.LogInformation("Monitor stopped");
    }

    public async Task<ServiceStatus> GetStatusAsync()
    {
        var state = await ComputeStateAsync();
        lock (_apiGate)
        {
            var counts = _api.Counts();
            return new ServiceStatus(
                IpcProtocol.Version,
                ServiceVersion,
                state,
                _userName,
                _api.BaselineAt,
                _lastPollAt,
                _lastError ?? _login.LastError,
                _api.TicketsInState(_pipeline.Mapping.ActiveState).Count,
                counts.GetValueOrDefault(OutboxStatus.Pending));
        }

    }

    public async Task<LoginChallenge> StartLoginAsync()
    {
        var challenge = await _login.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Raise(ChangeKind.Status);
        return challenge;
    }

    public async Task<DryRunResult> DryRunRulesAsync()
    {
        await _pipeline.EnsureMeAsync(_stopping.Token);
        var tickets = await _pipeline.Source.GetActiveTicketsAsync(_stopping.Token);
        var rules = _pipeline.Rules.Current;
        var context = new EvaluationContext(DateTimeOffset.UtcNow, _pipeline.Mapping, _pipeline.Me);
        var report = RuleEngine.DryRun(rules, RuleEngine.AsNewTicketEvents(tickets), context);

        return new DryRunResult(
            report.EventsEvaluated,
            [.. rules.Select(r => new RuleHitCount(r.Id, r.Name, report.MatchesByRule.GetValueOrDefault(r.Id)))],
            [.. report.Matches.Take(20).Select(m => new DryRunTicket(m.Event.TicketNumber ?? "-", m.Event.Title, m.Rule.Id))]);
    }

    public Task<IReadOnlyList<EventInfo>> GetRecentEventsAsync(int count)
    {
        lock (_apiGate)
        {
            return Task.FromResult<IReadOnlyList<EventInfo>>(
                _api.RecentEvents(Math.Clamp(count, 1, 500))
                    .Select(e => new EventInfo(
                        e.DedupKey, e.Type.ToString(), e.Subtype, e.TicketNumber, e.Title ?? e.Ticket?.Title, e.OccurredAt,
                        _api.IsEventAcknowledged(e.DedupKey)))
                    .ToList());
        }
    }

    public Task AcknowledgeAsync(string eventKey)
    {
        lock (_apiGate)
        {
            _api.AcknowledgeEvent(eventKey, DateTimeOffset.UtcNow);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NotificationInfo>> ClaimNotificationsAsync(IReadOnlyList<string> channels)
    {
        var claimed = new List<NotificationInfo>();
        var now = DateTimeOffset.UtcNow;

        lock (_apiGate)
        {
            foreach (var item in _api.Due(now, 100).Where(i => channels.Contains(i.Channel, StringComparer.OrdinalIgnoreCase)))
            {
                // Handed over (or dropped, for an escalation nobody needs any more): either way it leaves the queue.
                _api.MarkSent(item.Id, now);
                if (item.RequiresUnacknowledged && _api.IsEventAcknowledged(item.EventKey))
                {
                    continue;
                }

                claimed.Add(new NotificationInfo(
                    item.Id, item.EventKey, item.Channel, item.Notification.Title, item.Notification.Body,
                    item.Notification.Severity, item.Notification.Url));
            }
        }

        return Task.FromResult<IReadOnlyList<NotificationInfo>>(claimed);
    }

    /// <summary>Polls now instead of waiting out the interval (after a sign-in, for instance).</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _api.Dispose();
        _pipeline.Dispose();
        _stopping.Dispose();
    }

    private Task OnSignedInAsync(string user)
    {
        _userName = user;
        _needsLogin = false;
        _lastError = null;
        Wake();
        Raise(ChangeKind.Status);
        return Task.CompletedTask;
    }

    private TimeSpan Failed(TicketSourceException ex)
    {
        _failures++;
        _lastError = ex.Message;
        _needsLogin = ex.Failure == SourceFailure.AuthRequired;
        _logger.LogWarning("Poll failed ({Failure}): {Message}", ex.Failure, ex.Message);

        var now = DateTimeOffset.UtcNow;
        var published = _pipeline.Engine.Publish(_pipeline.Health.Failure(ex, now), now);
        if (published > 0)
        {
            Raise(ChangeKind.Notifications);
        }

        return PollingSchedule.AfterFailure(ex, _failures);
    }

    private async Task<ProfileState> ComputeStateAsync()
    {
        if (_login.InProgress)
        {
            return ProfileState.LoggingIn;
        }

        _userName ??= await _pipeline.Tokens.AccountNameAsync();
        if (_userName is null || _needsLogin)
        {
            return ProfileState.NeedsLogin;
        }

        if (_failures > 0)
        {
            return ProfileState.Degraded;
        }

        return _lastPollAt is null ? ProfileState.Starting : ProfileState.Running;
    }

    private async Task AnnounceIfStateChangedAsync()
    {
        var state = await ComputeStateAsync();
        if (_announced != state)
        {
            _announced = state;
            Raise(ChangeKind.Status);
        }
    }

    private void Raise(ChangeKind kind)
    {
        try
        {
            Changed?.Invoke(this, new ChangedEventArgs(kind));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A listener failed while handling {Kind}", kind);
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken stop)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var timer = Task.Delay(delay, cancel.Token);
        var wake = _wake.WaitAsync(cancel.Token);
        await Task.WhenAny(timer, wake);
        await cancel.CancelAsync();
        try
        {
            await Task.WhenAll(timer, wake);
        }
        catch (OperationCanceledException)
        {
            // The loser of the race was cancelled on purpose.
        }
    }
}
