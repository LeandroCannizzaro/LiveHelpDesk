using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>How hard the pill insists, growing while nobody answers it (Apri, Vista, 15 min).</summary>
public enum OverlayUrgency
{
    /// <summary>Just arrived: the plain heartbeat.</summary>
    Calm,

    /// <summary>Rings ripple out of the number, which gets an outline; the waiting time shows.</summary>
    Insistent,

    /// <summary>The whole pill's outline pulses and the full text opens up again.</summary>
    Pressing,

    /// <summary>A small shake now and then (and, for critical events, one reminder sound).</summary>
    Urgent,
}

/// <summary>What the floating pill shows. It only mirrors the <see cref="AttentionModel"/>; the heartbeat is a looping animation in the view.</summary>
public sealed partial class OverlayViewModel(Func<Task> open, Func<Task> acknowledgeAll, Action<TimeSpan> snooze) : ObservableObject
{
    // Unanswered for this long (attention; critical takes half) before each step up.
    private static readonly TimeSpan[] Steps = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(8)];
    private static readonly ImmutableSolidColorBrush AttentionBackground = new(Color.Parse("#F59E0B"));
    private static readonly ImmutableSolidColorBrush AttentionForeground = new(Color.Parse("#1F1300"));
    private static readonly ImmutableSolidColorBrush CriticalBackground = new(Color.Parse("#DC2626"));
    private static readonly ImmutableSolidColorBrush CriticalForeground = new(Colors.White);
    private static readonly ImmutableSolidColorBrush DefaultAccent = new(Color.FromArgb(0x33, 0, 0, 0));

    private UiSettings? _theme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCritical), nameof(IsAttention))]
    private AttentionLevel _level;

    [ObservableProperty]
    private string _title = "";

    /// <summary>The one-line summary shown while the pill is collapsed.</summary>
    [ObservableProperty]
    private string _subtitle = "";

    /// <summary>The full, multi-line body shown while the pill is expanded.</summary>
    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private string _countText = "";

    /// <summary>Expanded by default: shows the whole body; collapsing trims it to one line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleGlyph), nameof(ToggleTip))]
    private bool _isExpanded = true;

    [ObservableProperty]
    private IBrush _background = AttentionBackground;

    [ObservableProperty]
    private IBrush _foreground = AttentionForeground;

    [ObservableProperty]
    private IBrush _accent = DefaultAccent;

    public bool IsCritical => Level == AttentionLevel.Critical;

    public bool IsAttention => Level == AttentionLevel.Attention;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsistent), nameof(IsPressing), nameof(IsUrgent))]
    private OverlayUrgency _urgency;

    public bool IsInsistent => Urgency >= OverlayUrgency.Insistent;

    public bool IsPressing => Urgency >= OverlayUrgency.Pressing;

    public bool IsUrgent => Urgency >= OverlayUrgency.Urgent;

    /// <summary>"⏱ da 4 min" once the pill has waited a while; empty before.</summary>
    [ObservableProperty]
    private string _waitingText = "";

    public static OverlayUrgency UrgencyFor(TimeSpan unanswered, AttentionLevel level)
    {
        var factor = level == AttentionLevel.Critical ? 0.5 : 1;
        var urgency = OverlayUrgency.Calm;
        foreach (var step in Steps)
        {
            if (unanswered >= step * factor)
            {
                urgency++;
            }
        }

        return level == AttentionLevel.None ? OverlayUrgency.Calm : urgency;
    }

    public string ToggleGlyph => IsExpanded ? "\u25B2" : "\u25BC";

    public string ToggleTip => IsExpanded ? "Riduci a una riga" : "Mostra tutto";

    /// <summary>Gives the pill its colours; the same <see cref="UiSettings"/> instance is read again on every update.</summary>
    public void UseTheme(UiSettings settings)
    {
        _theme = settings;
        ApplyTheme();
    }

    public void Update(AttentionModel model, DateTimeOffset now)
    {
        Level = model.VisibleLevel(now);
        var headline = model.Headline;
        Title = headline?.Title ?? "";
        CountText = model.Count.ToString();

        var first = FirstLine(headline?.Body);
        Subtitle = model.Count > 1
            ? $"{first}{(string.IsNullOrWhiteSpace(headline?.Body) ? "" : " · ")}+{model.Count - 1} da gestire"
            : first;

        var full = (headline?.Body ?? "").Trim();
        Body = model.Count > 1
            ? $"{full}{(full.Length == 0 ? "" : "\n")}+{model.Count - 1} da gestire"
            : full;

        var unanswered = model.UnansweredFor(now);
        var urgency = UrgencyFor(unanswered, Level);
        if (urgency >= OverlayUrgency.Pressing && Urgency < OverlayUrgency.Pressing)
        {
            IsExpanded = true; // a pill reduced to one line opens up again: there may be more to read
        }

        Urgency = urgency;
        WaitingText = urgency >= OverlayUrgency.Insistent ? $"⏱ da {Math.Max(1, (int)unanswered.TotalMinutes)} min" : "";

        ApplyTheme();
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private Task OpenAsync() => open();

    [RelayCommand]
    private Task AcknowledgeAllAsync() => acknowledgeAll();

    [RelayCommand]
    private void SnoozeQuarterHour() => snooze(TimeSpan.FromMinutes(15));

    private void ApplyTheme()
    {
        var critical = Level == AttentionLevel.Critical;
        Background = Brush(_theme?.OverlayBackground, critical ? CriticalBackground : AttentionBackground);
        Foreground = Brush(_theme?.OverlayTextColor, critical ? CriticalForeground : AttentionForeground);
        Accent = Brush(_theme?.OverlayAccent, DefaultAccent);
    }

    private static IBrush Brush(string? hex, IBrush fallback) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color) ? new ImmutableSolidColorBrush(color) : fallback;

    private static string FirstLine(string? text) =>
        text is null ? "" : text.Split('\n', 2)[0].Trim();
}
