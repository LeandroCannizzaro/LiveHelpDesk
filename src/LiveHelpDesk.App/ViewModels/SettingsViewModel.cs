using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>This user's choices about the app itself: the overlay, the sound, which screen, starting with Windows. Applied as soon as they change.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly UiSettings _settings;
    private readonly AutoStart _autoStart;
    private readonly string _executable;
    private readonly Action _showOverlayDemo;
    private readonly Action _settingsChanged;
    private bool _loading = true;

    public SettingsViewModel(
        UiSettings settings, AutoStart autoStart, string executable, IReadOnlyList<string> screens, Action showOverlayDemo, Action settingsChanged)
    {
        _settings = settings;
        _autoStart = autoStart;
        _executable = executable;
        _showOverlayDemo = showOverlayDemo;
        _settingsChanged = settingsChanged;

        Screens = [new Choice("0", "Lo schermo principale"), .. screens.Select((name, i) => new Choice((i + 1).ToString(), name))];
        _overlayEnabled = settings.OverlayEnabled;
        _playSound = settings.PlaySoundForCritical;
        _screen = Screens.FirstOrDefault(c => c.Value == settings.OverlayScreen.ToString()) ?? Screens[0];
        _startWithWindows = autoStart.IsEnabled;
        _overlayBackground = settings.OverlayBackground;
        _overlayTextColor = settings.OverlayTextColor;
        _overlayAccent = settings.OverlayAccent;
        _loading = false;
    }

    public IReadOnlyList<Choice> Screens { get; }

    [ObservableProperty]
    private bool _overlayEnabled;

    [ObservableProperty]
    private bool _playSound;

    [ObservableProperty]
    private Choice _screen;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private string _overlayBackground = "";

    [ObservableProperty]
    private string _overlayTextColor = "";

    [ObservableProperty]
    private string _overlayAccent = "";

    partial void OnOverlayEnabledChanged(bool value) => Apply(() => _settings.OverlayEnabled = value);

    partial void OnPlaySoundChanged(bool value) => Apply(() => _settings.PlaySoundForCritical = value);

    partial void OnScreenChanged(Choice value) => Apply(() => _settings.OverlayScreen = int.Parse(value.Value));

    partial void OnOverlayBackgroundChanged(string value) => Apply(() => _settings.OverlayBackground = value.Trim());

    partial void OnOverlayTextColorChanged(string value) => Apply(() => _settings.OverlayTextColor = value.Trim());

    partial void OnOverlayAccentChanged(string value) => Apply(() => _settings.OverlayAccent = value.Trim());

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            _autoStart.Set(value, _executable);
            Note = value ? "Si avvierà da solo, nell'area di notifica, quando accedi a Windows." : "Non si avvierà più da solo.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Note = $"Impossibile cambiare l'avvio automatico: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetPosition()
    {
        _settings.OverlayOffsetX = 0;
        _settings.OverlayTop = 8;
        _settings.Save();
        _settingsChanged();
        Note = "La finestrella tornerà in alto al centro.";
    }

    [RelayCommand]
    private void ShowOverlayDemo() => _showOverlayDemo();

    private void Apply(Action change)
    {
        if (_loading)
        {
            return;
        }

        change();
        _settings.Save();
        _settingsChanged();
    }
}
