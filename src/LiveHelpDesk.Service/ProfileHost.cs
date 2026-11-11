using System.Collections.Concurrent;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Delivery;
using LiveHelpDesk.Core.Health;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Core.Sources;
using LiveHelpDesk.Core.State;
using LiveHelpDesk.Dynamics;
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
    private readonly LoginCoordinator _mailLogin;
    private readonly SqliteStateStore _dispatchStore; // the delivery loop's own connection
    private readonly OutboxDispatcher _dispatcher;
    private readonly SemaphoreSlim _dispatchWake = new(0, 1);
    private readonly ConcurrentQueue<TicketEvent> _channelHealth = new();
    private readonly ConcurrentDictionary<Guid, LookupItem> _names = new();

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
        _dispatchStore = new SqliteStateStore(Path.Combine(pipeline.ProfileDirectory, "state.db"));
        _dispatcher = new OutboxDispatcher(
            _dispatchStore, _dispatchStore, id => pipeline.Channels.GetForDelivery(id), new DispatcherOptions(), logger,
            channel => pipeline.ChannelStore.Current.MaxPerHour.GetValueOrDefault(channel, 20));
        _login = new LoginCoordinator(
            (show, ct) => pipeline.Tokens.LoginWithDeviceCodeAsync(show, ct), OnSignedInAsync, logger, _stopping.Token);
        _mailLogin = new LoginCoordinator(
            (show, ct) => pipeline.Outlook.LoginWithDeviceCodeAsync(
                info => show(new DeviceCodeInfo(info.UserCode, info.VerificationUrl, info.ExpiresOn, info.Message)), ct),
            OnMailSignedInAsync, logger, _stopping.Token);
    }

    public event EventHandler<ChangedEventArgs>? Changed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var stop = linked.Token;
        _logger.LogInformation("Monitor started (every {Seconds}s)", PollInterval.TotalSeconds);
        var delivering = DeliverAsync(stop);

        while (!stop.IsCancellationRequested)
        {
            TimeSpan delay;
            PublishChannelHealth();
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
                    WakeDelivery();
                }

                delay = PollingSchedule.AfterSuccess(PollInterval, Random.Shared.NextDouble());
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

        try
        {
            await delivering;
        }
        catch (OperationCanceledException)
        {
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
                counts.GetValueOrDefault(OutboxStatus.Pending),
                _pipeline.Settings.HomeUrl);
        }

    }

    public async Task<LoginChallenge> StartLoginAsync()
    {
        var challenge = await _login.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Raise(ChangeKind.Status);
        return challenge;
    }

    public async Task<LoginChallenge> StartMailLoginAsync()
    {
        var challenge = await _mailLogin.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Raise(ChangeKind.Status);
        return challenge;
    }

    public async Task<MailAccountInfo> GetMailAccountAsync()
    {
        string? account = null;
        try
        {
            account = await _pipeline.Outlook.AccountNameAsync(_stopping.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not read the Outlook account: {Message}", ex.Message);
        }

        return new MailAccountInfo(account, _mailLogin.InProgress, _mailLogin.LastError);
    }

    public async Task SignOutMailAsync()
    {
        await _pipeline.Outlook.SignOutAsync();
        _logger.LogInformation("Signed out of Outlook");
        Raise(ChangeKind.Status);
    }

    private Task OnMailSignedInAsync(string user)
    {
        Raise(ChangeKind.Status);
        WakeDelivery(); // messages held back for the missing sign-in may be sendable now
        return Task.CompletedTask;
    }

    public Task<DryRunResult> DryRunRulesAsync() => DryRunAsync(_pipeline.Rules.Current);

    public Task<DryRunResult> DryRunDraftAsync(string rulesJson)
    {
        var parsed = RulesConfig.Parse(rulesJson);
        if (!parsed.IsValid)
        {
            throw new ArgumentException(string.Join(" ", parsed.Errors));
        }

        return DryRunAsync(parsed.File!.Rules);
    }

    public Task<string> GetRulesJsonAsync() => Task.FromResult(_pipeline.Rules.ReadJson());

    public Task<RulesSaveResult> SaveRulesJsonAsync(string json)
    {
        var result = _pipeline.Rules.Replace(json);
        if (result.IsValid)
        {
            _logger.LogInformation("Rules replaced from the app ({Count} rules)", result.File!.Rules.Count);
            Raise(ChangeKind.Status);
        }

        return Task.FromResult(new RulesSaveResult(result.IsValid, result.Errors));
    }

    public Task<string> GetTemplatesJsonAsync() => Task.FromResult(_pipeline.Templates.ReadJson());

    public Task<RulesSaveResult> SaveTemplatesJsonAsync(string json)
    {
        var result = _pipeline.Templates.Replace(json);
        if (result.IsValid)
        {
            _logger.LogInformation("Notification texts replaced from the app ({Count} texts)", result.File!.Templates.Count);
        }

        return Task.FromResult(new RulesSaveResult(result.IsValid, result.Errors));
    }

    public async Task<IReadOnlyList<LookupItem>> GetLookupsAsync()
    {
        IReadOnlyList<TicketSnapshot> active;
        lock (_apiGate)
        {
            active = _api.TicketsInState(_pipeline.Mapping.ActiveState);
        }

        var wanted = active
            .SelectMany(t => new[] { t.OwnerId, t.QueueId, t.Level2UserId })
            .OfType<Guid>()
            .Distinct()
            .ToList();

        var missing = wanted.Where(id => !_names.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                foreach (var (id, name, kind) in await _pipeline.Source.ResolveNamesAsync(missing, _stopping.Token))
                {
                    _names[id] = new LookupItem(id.ToString("D"), name, kind);
                }
            }
            catch (TicketSourceException ex)
            {
                _logger.LogWarning("Could not look up names: {Message}", ex.Message);
            }
        }

        return [.. wanted.Where(_names.ContainsKey).Select(id => _names[id]).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private async Task<DryRunResult> DryRunAsync(IReadOnlyList<Rule> rules)
    {
        await _pipeline.EnsureMeAsync(_stopping.Token);
        var tickets = await _pipeline.Source.GetActiveTicketsAsync(_stopping.Token);
        var context = new EvaluationContext(DateTimeOffset.UtcNow, _pipeline.Mapping, _pipeline.Me);
        var report = RuleEngine.DryRun(rules, RuleEngine.AsNewTicketEvents(tickets), context);

        return new DryRunResult(
            report.EventsEvaluated,
            [.. rules.Select(r => new RuleHitCount(r.Id, r.Name, report.MatchesByRule.GetValueOrDefault(r.Id)))],
            [.. report.Matches
                .GroupBy(m => m.Rule.Id)
                .SelectMany(g => g.Take(5))
                .Select(m => new DryRunTicket(m.Event.TicketNumber ?? "-", m.Event.Title, m.Rule.Id))]);
    }

    public Task<ChannelSettings> GetChannelSettingsAsync() =>
        Task.FromResult(ChannelStore.Masked(_pipeline.ChannelStore.Current));

    public Task SaveChannelSettingsAsync(ChannelSettings settings)
    {
        _pipeline.ChannelStore.Save(settings);
        Raise(ChangeKind.Status);
        WakeDelivery(); // messages held back for a missing channel may be sendable now
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChannelInfo>> GetChannelStatusAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var live = _dispatcher.Status(now).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var settings = _pipeline.ChannelStore.Current;
        IReadOnlyDictionary<string, int> pending;
        lock (_apiGate)
        {
            pending = _api.PendingByChannel();
        }

        var info = new[]
        {
            ("email", settings.Email.Enabled),
            ("teams", settings.Teams.Enabled),
            ("sms", settings.Sms.Enabled),
            ("whatsapp", settings.WhatsApp.Enabled),
        }.Select(channel =>
        {
            live.TryGetValue(channel.Item1, out var status);
            var held = channel.Item1 == "email" && channel.Item2 && _pipeline.Channels.IsHeldForSignIn();
            return new ChannelInfo(
                channel.Item1,
                channel.Item2,
                (status?.Breaker ?? BreakerState.Closed).ToString(),
                status?.LastSuccess,
                status?.LastFailure,
                held ? "serve l'accesso a Outlook: i messaggi aspettano in coda" : status?.LastError,
                status?.SentLastHour ?? 0,
                pending.GetValueOrDefault(channel.Item1));
        }).ToList();

        return Task.FromResult<IReadOnlyList<ChannelInfo>>(info);
    }

    public async Task<ChannelTestResult> TestChannelAsync(string channelId)
    {
        var channel = _pipeline.Channels.Get(channelId);
        if (channel is null)
        {
            return new ChannelTestResult(false, "Il canale non è attivo o non è configurato.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await channel.TestAsync(timeout.Token);
            return new ChannelTestResult(result.Success, result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            return new ChannelTestResult(false, ex is OperationCanceledException ? "Nessuna risposta entro 30 secondi." : ex.Message);
        }
    }

    public Task<IReadOnlyList<EventInfo>> GetRecentEventsAsync(int count)
    {
        lock (_apiGate)
        {
            return Task.FromResult<IReadOnlyList<EventInfo>>(
                _api.RecentEvents(Math.Clamp(count, 1, 500))
                    .Select(e => new EventInfo(
                        e.DedupKey, e.Type.ToString(), e.Subtype, e.TicketNumber, e.Title ?? e.Ticket?.Title, e.OccurredAt,
                        _api.IsEventAcknowledged(e.DedupKey),
                        e.TicketId is { } id ? _pipeline.Settings.Dynamics.IncidentUri(id) : null))
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

    public const int MinPollSeconds = 10;
    public const int MaxPollSeconds = 3600;
    private const string PollSecondsKey = "poll_seconds";

    /// <summary>The user's own interval (0 = none chosen), read once from the profile and then kept here.</summary>
    private int _pollOverride = -1;

    private int DefaultPollSeconds => Math.Max(5, _pipeline.Settings.PollSeconds);

    private TimeSpan PollInterval
    {
        get
        {
            if (_pollOverride < 0)
            {
                lock (_apiGate)
                {
                    _pollOverride = int.TryParse(_api.GetValue(PollSecondsKey), out var stored) ? Math.Max(0, stored) : 0;
                }
            }

            return TimeSpan.FromSeconds(_pollOverride > 0 ? _pollOverride : DefaultPollSeconds);
        }
    }

    public Task<PollSettings> GetPollSettingsAsync() =>
        Task.FromResult(new PollSettings((int)PollInterval.TotalSeconds, DefaultPollSeconds, MinPollSeconds, MaxPollSeconds));

    public Task<PollSettings> SetPollSecondsAsync(int seconds)
    {
        var value = seconds <= 0 ? 0 : Math.Clamp(seconds, MinPollSeconds, MaxPollSeconds);
        lock (_apiGate)
        {
            _api.SetValue(PollSecondsKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _pollOverride = value;
        }

        _logger.LogInformation("Polling interval set to {Seconds}s", PollInterval.TotalSeconds);
        Wake(); // the wait in progress was computed with the old interval
        return GetPollSettingsAsync();
    }

    public Task<IReadOnlyList<NotificationInfo>> ClaimNotificationsAsync(IReadOnlyList<string> channels)
    {
        var claimed = new List<NotificationInfo>();
        var now = DateTimeOffset.UtcNow;

        lock (_apiGate)
        {
            foreach (var item in _api.Due(now, 100).Where(i => channels.Contains(i.Channel, StringComparer.OrdinalIgnoreCase)))
            {
                if (item.RequiresUnacknowledged && _api.IsEventAcknowledged(item.EventKey))
                {
                    _api.MarkCancelled(item.Id, "già riconosciuto", now);
                    continue;
                }

                _api.MarkSent(item.Id, now);

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

    /// <summary>Sends what the rules queued for the outside channels, every few seconds and whenever something is queued.</summary>
    private async Task DeliverAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var summary = await _dispatcher.RunOnceAsync(DateTimeOffset.UtcNow, stop);
                foreach (var problem in summary.Health)
                {
                    _channelHealth.Enqueue(problem); // published by the polling loop, which owns the rules' connection
                }

                if (summary.Sent + summary.Failed + summary.Cancelled > 0)
                {
                    _logger.LogInformation(
                        "Delivery: {Sent} sent, {Failed} failed ({Dead} given up), {Cancelled} cancelled, {Digests} summaries",
                        summary.Sent, summary.Failed, summary.Dead, summary.Cancelled, summary.Digests);
                    Raise(ChangeKind.Status);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delivery round failed");
            }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var timer = Task.Delay(TimeSpan.FromSeconds(5), cancel.Token);
            var wake = _dispatchWake.WaitAsync(cancel.Token);
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

    private void PublishChannelHealth()
    {
        var problems = new List<TicketEvent>();
        while (_channelHealth.TryDequeue(out var evt))
        {
            problems.Add(evt);
        }

        if (problems.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            _pipeline.Engine.Publish(problems, now);
            Raise(ChangeKind.Notifications);
        }
    }

    private void WakeDelivery()
    {
        try
        {
            _dispatchWake.Release();
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
        _dispatchStore.Dispose();
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
            WakeDelivery();
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
