using System.Globalization;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Detection;
using LiveHelpDesk.Core.Notifications;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Core.Sources;
using LiveHelpDesk.Core.State;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Core.Polling;

public sealed record PollingOptions
{
    /// <summary>Re-read this much before the watermark: rows can become visible a little late.</summary>
    public TimeSpan Overlap { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>If the monitor was blind for longer than this, realign instead of replaying hours of changes.</summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan UnattendedAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How often to check that the tickets we think are active still are.</summary>
    public TimeSpan ReconcileEvery { get; init; } = TimeSpan.FromMinutes(15);
}

/// <param name="Events">Everything new this round, including health events.</param>
/// <param name="Queued">Outbox rows added (suppressed ones excluded).</param>
public sealed record PollResult(IReadOnlyList<TicketEvent> Events, int Queued, bool BaselineTaken);

/// <summary>
/// One polling round: read what changed since the watermarks, detect events, run the rules, queue the
/// notifications. Knows nothing about HTTP or Windows, so a whole day of traffic can be replayed in a test.
/// </summary>
public sealed class PollingEngine(
    ITicketSource source,
    ITicketStateStore store,
    IOutbox outbox,
    TicketDetector detector,
    NotificationPlanner planner,
    TicketMapping mapping,
    PollingOptions options,
    Func<IReadOnlyList<Rule>> rules,
    Func<Guid?> currentUser,
    ILogger logger)
{
    private const string IncidentWatermark = "wm.incident";
    private const string MessageWatermark = "wm.message";
    private const string LastReconcile = "last_reconcile";

    public async Task<PollResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        var now = await source.GetServerTimeAsync(cancellationToken);

        if (store.BaselineAt is not { } baselineAt)
        {
            return await BaselineAsync(now, [], cancellationToken);
        }

        var incidentMark = ReadTime(IncidentWatermark) ?? baselineAt;
        var messageMark = ReadTime(MessageWatermark) ?? baselineAt;
        if (now - Min(incidentMark, messageMark) > options.MaxGap)
        {
            logger.LogWarning("Monitor blind since {Since:O}: realigning instead of replaying the gap", Min(incidentMark, messageMark));
            var gap = new TicketEvent(
                $"health:GapDetected:{now.UtcTicks}", TicketEventType.MonitorHealth, now,
                Subtype: "GapDetected", Change: $"Nessun controllo dal {Min(incidentMark, messageMark).ToLocalTime():g}");
            return await BaselineAsync(now, [gap], cancellationToken);
        }

        var tickets = await source.GetTicketsModifiedSinceAsync(incidentMark - options.Overlap, cancellationToken);
        var messages = await source.GetMessagesCreatedSinceAsync(messageMark - options.Overlap, cancellationToken);

        var events = new List<TicketEvent>();
        var detection = detector.Detect(tickets, messages);
        events.AddRange(detection.Events);

        if (detection.UnresolvedMessages.Count > 0)
        {
            // The ticket behind a new message was not known yet: fetch it, remember it silently, retry once.
            var ids = detection.UnresolvedMessages.Select(m => m.TicketId).Distinct().ToList();
            detector.Remember(await source.GetTicketsAsync(ids, cancellationToken));
            events.AddRange(detector.Detect([], detection.UnresolvedMessages).Events);
        }

        events.AddRange(UnattendedEvaluator.Evaluate(store, mapping, options.UnattendedAfter, now));

        if (now - (ReadTime(LastReconcile) ?? baselineAt) >= options.ReconcileEvery)
        {
            events.AddRange(await ReconcileAsync(now, cancellationToken));
        }

        // Anything modified after "now" is picked up next round, so the mark can move up to "now - overlap".
        WriteTime(IncidentWatermark, Max(incidentMark, now - options.Overlap));
        WriteTime(MessageWatermark, Max(messageMark, now - options.Overlap));

        return new PollResult(events, Publish(events, now), false);
    }

    /// <summary>Runs events raised elsewhere (health problems) through the same rules and outbox.</summary>
    public int Publish(IEnumerable<TicketEvent> events, DateTimeOffset now)
    {
        var queued = 0;
        var context = new EvaluationContext(now, mapping, currentUser());
        var current = rules();

        foreach (var evt in events)
        {
            // Events from the detector are registered already; health events arrive here for the first time.
            store.TryRegisterEvent(evt);

            foreach (var match in RuleEngine.Evaluate(evt, current, context))
            {
                foreach (var item in planner.Plan(match, now))
                {
                    if (outbox.TryAdd(item) && item.Status == OutboxStatus.Pending)
                    {
                        queued++;
                    }
                }

                logger.LogInformation(
                    "Rule {Rule} matched {Type}{Subtype} on {Ticket}",
                    match.Rule.Id, evt.Type, evt.Subtype is null ? "" : $"/{evt.Subtype}", evt.TicketNumber ?? "-");
            }
        }

        return queued;
    }

    private async Task<PollResult> BaselineAsync(DateTimeOffset now, IReadOnlyList<TicketEvent> extra, CancellationToken cancellationToken)
    {
        var active = await source.GetActiveTicketsAsync(cancellationToken);
        var result = detector.Baseline(active, now);

        WriteTime(IncidentWatermark, now);
        WriteTime(MessageWatermark, now);
        WriteTime(LastReconcile, now);
        logger.LogInformation("Baseline taken: {Count} active tickets remembered, no notifications sent", active.Count);

        var events = extra.Concat(result.Events).ToList();
        return new PollResult(events, Publish(events, now), true);
    }

    /// <summary>
    /// The watermark never sees a ticket vanish (merged, deleted, access lost) or one that slipped through
    /// unseen. Comparing the active set with what we remember catches both.
    /// </summary>
    private async Task<IReadOnlyList<TicketEvent>> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var events = new List<TicketEvent>();
        var serverIds = (await source.GetActiveTicketIdsAsync(cancellationToken)).ToHashSet();
        var known = store.TicketsInState(mapping.ActiveState).Select(t => t.Id).ToHashSet();

        var missing = known.Where(id => !serverIds.Contains(id)).ToList();
        var unseen = serverIds.Where(id => !known.Contains(id) && store.GetTicket(id) is null).ToList();

        if (missing.Count > 0 || unseen.Count > 0)
        {
            var fetched = await source.GetTicketsAsync([.. missing, .. unseen], cancellationToken);
            events.AddRange(detector.Detect(fetched, []).Events);

            var found = fetched.Select(t => t.Id).ToHashSet();
            foreach (var id in missing.Where(id => !found.Contains(id)))
            {
                if (detector.MarkDisappeared(id, now) is { } gone)
                {
                    events.Add(gone);
                }
            }
        }

        WriteTime(LastReconcile, now);
        return events;
    }

    private DateTimeOffset? ReadTime(string key) =>
        store.GetValue(key) is { } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    private void WriteTime(string key, DateTimeOffset time) =>
        store.SetValue(key, time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
