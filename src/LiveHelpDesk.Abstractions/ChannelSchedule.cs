namespace LiveHelpDesk.Abstractions;

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
