using System.Net;
using LiveHelpDesk.Channels;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Detection;
using LiveHelpDesk.Core.Health;
using LiveHelpDesk.Core.Notifications;
using LiveHelpDesk.Core.Polling;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Core.State;
using LiveHelpDesk.Dynamics;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>Everything one user profile needs, wired together: used by the worker and by the command line.</summary>
public sealed class Pipeline : IDisposable
{
    private readonly MeHolder _holder;

    private Pipeline(MeHolder holder) => _holder = holder;

    public required ServiceSettings Settings { get; init; }

    public required string ProfileDirectory { get; init; }

    public required SqliteStateStore Store { get; init; }

    public required MsalTokenProvider Tokens { get; init; }

    public required DynamicsTicketSource Source { get; init; }

    public required PollingEngine Engine { get; init; }

    public required HealthMonitor Health { get; init; }

    public required RulesProvider Rules { get; init; }

    /// <summary>The user-written notification texts.</summary>
    public required TemplatesProvider Templates { get; init; }

    public required TicketMapping Mapping { get; init; }

    public required ChannelStore ChannelStore { get; init; }

    public required ChannelRegistry Channels { get; init; }

    /// <summary>The Outlook sign-in used by the e-mail channel (kept apart from the Dynamics one).</summary>
    public required OutlookAuth Outlook { get; init; }

    /// <summary>The signed-in user's Dataverse id, known after the first successful call.</summary>
    public Guid? Me => _holder.Me;

    public static async Task<Pipeline> CreateAsync(string profileDirectory, string configPath, ILoggerFactory loggers)
    {
        var settings = ServiceSettings.LoadOrCreate(configPath);
        var options = settings.Dynamics;
        Directory.CreateDirectory(profileDirectory);

        var store = new SqliteStateStore(Path.Combine(profileDirectory, "state.db"));
        var tokens = await MsalTokenProvider.CreateAsync(options, profileDirectory);
        var mapping = TicketMapping.Qgs;
        var rules = new RulesProvider(Path.Combine(profileDirectory, "rules.json"), loggers.CreateLogger<RulesProvider>());
        var templates = new TemplatesProvider(Path.Combine(profileDirectory, "templates.json"), loggers.CreateLogger<TemplatesProvider>());

        // Behind a corporate proxy the service has no user settings: use the system proxy with default credentials.
        var handler = new SocketsHttpHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials };
        if (!string.IsNullOrWhiteSpace(settings.Proxy))
        {
            handler.Proxy = new WebProxy(settings.Proxy) { Credentials = CredentialCache.DefaultCredentials };
        }

        var http = new HttpClient(handler)
        {
            BaseAddress = options.ApiBase,
        };

        var channelStore = new ChannelStore(Path.Combine(profileDirectory, "channels.json"), loggers.CreateLogger<ChannelStore>());
        var channelHttp = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        var outlook = new OutlookAuth(profileDirectory, () => channelStore.Current.Email.Outlook, settings.TenantId);
        var channels = new ChannelRegistry(channelStore, channelHttp, outlookTokens: outlook);
        await outlook.AccountNameAsync(); // learn whether someone is signed in, so mail is held rather than failed

        var holder = new MeHolder();
        var source = new DynamicsTicketSource(new DataverseClient(http, tokens), DynamicsFieldMap.Qgs, () => holder.Me, settings.QueueIds);
        var planner = new NotificationPlanner(
            mapping,
            () => new ChannelPolicy { Channels = channelStore.Current.Schedules },
            id => options.IncidentUri(id),
            () => templates.Current);
        var engine = new PollingEngine(
            source, store, store, new TicketDetector(store, mapping), planner, mapping, settings.Polling,
            () => rules.Current, () => holder.Me, loggers.CreateLogger<PollingEngine>());

        return new Pipeline(holder)
        {
            Settings = settings,
            ProfileDirectory = profileDirectory,
            Store = store,
            Tokens = tokens,
            Source = source,
            Engine = engine,
            Health = new HealthMonitor(),
            Rules = rules,
            Templates = templates,
            Mapping = mapping,
            ChannelStore = channelStore,
            Channels = channels,
            Outlook = outlook,
        };
    }

    /// <summary>Asks Dynamics who we are, once; "$me" in rules and our own notes depend on it.</summary>
    public async Task EnsureMeAsync(CancellationToken cancellationToken)
    {
        _holder.Me ??= await Source.WhoAmIAsync(cancellationToken);
    }

    public void Dispose() => Store.Dispose();

    private sealed class MeHolder
    {
        public Guid? Me { get; set; }
    }
}
