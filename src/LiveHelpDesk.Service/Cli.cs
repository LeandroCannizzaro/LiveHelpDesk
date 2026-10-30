using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Outbox;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Core.Sources;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>Command line helpers for the interim period before the app can drive the service.</summary>
public static class Cli
{
    public static async Task<int> LoginAsync(Pipeline pipeline)
    {
        Console.WriteLine($"Accesso a {pipeline.Settings.Org} (tenant {pipeline.Settings.TenantId})");
        var user = await pipeline.Tokens.LoginWithDeviceCodeAsync(
            message =>
            {
                Console.WriteLine();
                Console.WriteLine(message);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await pipeline.EnsureMeAsync(CancellationToken.None);
        Console.WriteLine($"Accesso riuscito come {user} (utente Dynamics {pipeline.Me}).");
        return 0;
    }

    public static Task<int> StatusAsync(Pipeline pipeline)
    {
        var store = pipeline.Store;
        Console.WriteLine($"Profilo:        {pipeline.ProfileDirectory}");
        Console.WriteLine($"Baseline:       {store.BaselineAt?.ToLocalTime().ToString("g") ?? "non ancora eseguita"}");
        Console.WriteLine($"Ticket attivi:  {store.TicketsInState(pipeline.Mapping.ActiveState).Count}");
        Console.WriteLine($"Watermark:      ticket {store.GetValue("wm.incident") ?? "-"} · messaggi {store.GetValue("wm.message") ?? "-"}");
        Console.WriteLine($"Regole attive:  {pipeline.Rules.Current.Count(r => r.Enabled)} di {pipeline.Rules.Current.Count}");

        var counts = pipeline.Store.Counts();
        Console.WriteLine("Coda notifiche: " + string.Join(", ", Enum.GetValues<OutboxStatus>().Select(s => $"{s}={counts.GetValueOrDefault(s)}")));

        Console.WriteLine();
        Console.WriteLine("Ultimi eventi:");
        foreach (var evt in store.RecentEvents(15))
        {
            Console.WriteLine($"  {evt.OccurredAt.ToLocalTime():g}  {evt.Type,-22} {evt.Subtype,-14} {evt.TicketNumber}  {Trim(evt.Title)}");
        }

        return Task.FromResult(0);
    }

    /// <summary>Tries the rules on today's tickets as if each had just been created. Reads Dynamics, changes nothing.</summary>
    public static async Task<int> DryRunAsync(Pipeline pipeline)
    {
        await pipeline.EnsureMeAsync(CancellationToken.None);
        var tickets = await pipeline.Source.GetActiveTicketsAsync(CancellationToken.None);
        var context = new EvaluationContext(DateTimeOffset.UtcNow, pipeline.Mapping, pipeline.Me);
        var report = RuleEngine.DryRun(pipeline.Rules.Current, RuleEngine.AsNewTicketEvents(tickets), context);

        Console.WriteLine($"{report.EventsEvaluated} ticket attivi valutati come \"nuovo ticket\".");
        foreach (var (rule, count) in report.MatchesByRule)
        {
            Console.WriteLine($"  regola {rule}: {count} corrispondenze");
        }

        foreach (var match in report.Matches.Take(10))
        {
            Console.WriteLine($"    #{match.Event.TicketNumber}  {Trim(match.Event.Title)}  → {match.Rule.Id}");
        }

        return 0;
    }

    public static ILoggerFactory ConsoleLogger() =>
        LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

    private static string Trim(string? text) => text is { Length: > 50 } ? text[..50] + "…" : text ?? "";
}
