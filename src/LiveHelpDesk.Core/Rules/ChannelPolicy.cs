using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

/// <summary>An allowed time span. <see cref="From"/> after <see cref="To"/> means it crosses midnight.</summary>
public sealed class ChannelWindow
{
    /// <summary>Days the window starts on; empty means every day.</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    public TimeOnly From { get; set; } = TimeOnly.MinValue;

    public TimeOnly To { get; set; } = TimeOnly.MaxValue;
}

public sealed class ChannelSchedule
{
    /// <summary>The channel may be used only inside these windows; empty means always.</summary>
    public List<ChannelWindow> Windows { get; set; } = [];

    /// <summary>At or above this severity the windows are ignored (e.g. critical events always get through).</summary>
    public Severity? AlwaysAllowFrom { get; set; }
}

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
