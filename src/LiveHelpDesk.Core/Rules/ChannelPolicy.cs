using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

/// <summary>Quiet hours per channel (e.g. SMS only outside working hours, unless the event is critical).</summary>
public sealed class ChannelPolicy
{
    public Dictionary<string, ChannelSchedule> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="localNow">Wall-clock time of the user, not UTC.</param>
    public bool Allows(string channel, Severity severity, DateTimeOffset localNow)
    {
        if (!Channels.TryGetValue(channel, out var schedule) || schedule.Windows.Count == 0)
        {
            return true;
        }

        if (schedule.AlwaysAllowFrom is { } bypass && severity >= bypass)
        {
            return true;
        }

        return schedule.Windows.Any(w => Contains(w, localNow));
    }

    private static bool Contains(ChannelWindow window, DateTimeOffset now)
    {
        var time = TimeOnly.FromDateTime(now.DateTime);
        bool OnDay(DayOfWeek day) => window.Days.Count == 0 || window.Days.Contains(day);

        if (window.From <= window.To)
        {
            return OnDay(now.DayOfWeek) && time >= window.From && time < window.To;
        }

        // Crosses midnight: the evening part belongs to the starting day, the morning part to the next one.
        var previousDay = (DayOfWeek)(((int)now.DayOfWeek + 6) % 7);
        return (OnDay(now.DayOfWeek) && time >= window.From) || (OnDay(previousDay) && time < window.To);
    }
}
