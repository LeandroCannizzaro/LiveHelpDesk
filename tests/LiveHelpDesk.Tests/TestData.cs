using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Tests;

internal static class T
{
    public static readonly DateTimeOffset Baseline = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    public static DateTimeOffset At(int minutesFromBaseline) => Baseline.AddMinutes(minutesFromBaseline);

    /// <summary>A ticket that already existed at the baseline, unless <paramref name="created"/> says otherwise.</summary>
    public static TicketSnapshot Ticket(
        int id = 1,
        int state = 0,
        int status = 717600002,
        int priority = 2,
        Guid? owner = null,
        Guid? queue = null,
        Guid? level2 = null,
        int? comm = null,
        bool reminder = false,
        bool onHold = false,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null,
        string title = "Richiesta di supporto",
        IReadOnlyDictionary<string, string?>? extra = null) =>
        new(
            Id(id), $"2610{id:00000}", title, state, status, priority, owner, queue, level2, null, comm, reminder, onHold,
            created ?? Baseline.AddDays(-2),
            modified ?? created ?? Baseline.AddDays(-2))
        {
            Extra = extra ?? new Dictionary<string, string?>(),
        };

    public static MessageSnapshot Message(
        int id, int ticket, bool incoming = true, int atMinutes = 1, MessageKind kind = MessageKind.Email) =>
        new(Id(1000 + id), Id(ticket), kind, incoming, At(atMinutes), "cliente@example.com", "Re: richiesta");
}
