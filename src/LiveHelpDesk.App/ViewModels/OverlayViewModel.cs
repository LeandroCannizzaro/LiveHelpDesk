using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>What the floating pill shows. It only mirrors the <see cref="AttentionModel"/>; the pulse is driven by a timer outside.</summary>
public sealed partial class OverlayViewModel(Func<Task> open, Func<Task> acknowledgeAll, Action<TimeSpan> snooze) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCritical), nameof(IsAttention))]
    private AttentionLevel _level;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _countText = "";

    /// <summary>Alternates on a timer: the "blink".</summary>
    [ObservableProperty]
    private bool _pulse;

    public bool IsCritical => Level == AttentionLevel.Critical;

    public bool IsAttention => Level == AttentionLevel.Attention;

    public void Update(AttentionModel model, DateTimeOffset now)
    {
        Level = model.VisibleLevel(now);
        var headline = model.Headline;
        Title = headline?.Title ?? "";
        CountText = model.Count.ToString();
        Subtitle = model.Count > 1
            ? $"{FirstLine(headline?.Body)}{(string.IsNullOrWhiteSpace(headline?.Body) ? "" : " · ")}+{model.Count - 1} da gestire"
            : FirstLine(headline?.Body);
    }

    [RelayCommand]
    private Task OpenAsync() => open();

    [RelayCommand]
    private Task AcknowledgeAllAsync() => acknowledgeAll();

    [RelayCommand]
    private void SnoozeQuarterHour() => snooze(TimeSpan.FromMinutes(15));

    private static string FirstLine(string? text) =>
        text is null ? "" : text.Split('\n', 2)[0].Trim();
}
