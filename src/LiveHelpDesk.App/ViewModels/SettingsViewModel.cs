using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Ipc;

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

        Screens =
        [
            new Choice("0", "Lo schermo principale"),
            .. screens.Select((name, i) => new Choice((i + 1).ToString(), name)),
            new Choice(UiSettings.AllScreens.ToString(), "Tutti gli schermi"),
        ];
        _overlayEnabled = settings.OverlayEnabled;
        _playSound = settings.PlaySoundForCritical;
        _suppressWhenPresenting = settings.SuppressWhenPresenting;
        _suppressWhenOnVpn = settings.SuppressWhenOnVpn;
        _screen = Screens.FirstOrDefault(c => c.Value == settings.OverlayScreen.ToString()) ?? Screens[0];
        _startWithWindows = autoStart.IsEnabled;
        _overlayBackground = settings.OverlayBackground;
        _overlayTextColor = settings.OverlayTextColor;
        _overlayAccent = settings.OverlayAccent;
        _loading = false;
    }

    public IReadOnlyList<Choice> Screens { get; }

    /// <summary>The "Aggiornamenti" section; set by the host once the update checker exists.</summary>
    public UpdatesViewModel? Updates { get; set; }

    [ObservableProperty]
    private bool _overlayEnabled;

    [ObservableProperty]
    private bool _playSound;

    [ObservableProperty]
    private bool _suppressWhenPresenting;

    [ObservableProperty]
    private bool _suppressWhenOnVpn;

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

    partial void OnSuppressWhenPresentingChanged(bool value) => Apply(() => _settings.SuppressWhenPresenting = value);

    partial void OnSuppressWhenOnVpnChanged(bool value) => Apply(() => _settings.SuppressWhenOnVpn = value);

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

    /// <summary>"Automatico": clears one overlay colour so the urgency default applies again.</summary>
    [RelayCommand]
    private void ResetColor(string? which)
    {
        switch (which)
        {
            case "background":
                OverlayBackground = "";
                break;
            case "text":
                OverlayTextColor = "";
                break;
            case "accent":
                OverlayAccent = "";
                break;
        }
    }

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

    // ---- polling interval (kept by the monitor, per user) -------------------------------------------------------

    /// <summary>The monitor, for the polling interval; set by the host.</summary>
    public Func<ILiveHelpDeskApi?> Service { get; set; } = () => null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPollCommand))]
    private bool _pollAvailable;

    [ObservableProperty]
    private decimal? _pollSeconds;

    [ObservableProperty]
    private string _pollNote = "";

    public async Task LoadPollAsync()
    {
        if (Service() is not { } api)
        {
            PollAvailable = false;
            PollNote = "Il servizio non è raggiungibile: l'intervallo si potrà cambiare quando sarà in funzione.";
            return;
        }

        try
        {
            ShowPoll(await api.GetPollSettingsAsync());
            PollAvailable = true;
        }
        catch (Exception ex) when (IsServiceFailure(ex))
        {
            PollAvailable = false;
            PollNote = ex is StreamJsonRpc.RemoteMethodNotFoundException
                ? "Il servizio in esecuzione è di una versione precedente: aggiornalo per cambiare l'intervallo."
                : $"Impossibile leggere l'intervallo: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(PollAvailable))]
    private async Task ApplyPollAsync()
    {
        if (Service() is not { } api)
        {
            return;
        }

        try
        {
            var applied = await api.SetPollSecondsAsync((int)(PollSeconds ?? 0));
            ShowPoll(applied);
            Note = $"I nuovi eventi vengono cercati ogni {applied.Seconds} secondi.";
        }
        catch (Exception ex) when (IsServiceFailure(ex))
        {
            PollNote = $"Impossibile cambiare l'intervallo: {ex.Message}";
        }
    }

    private void ShowPoll(PollSettings poll)
    {
        PollSeconds = poll.Seconds;
        PollNote = $"Predefinito: {poll.DefaultSeconds} secondi; da {poll.MinSeconds} a {poll.MaxSeconds}. Un intervallo breve avvisa prima ma interroga Dynamics più spesso.";
    }

    private static bool IsServiceFailure(Exception ex) =>
        ex is StreamJsonRpc.RemoteRpcException or IOException or ObjectDisposedException;

    // ---- back to defaults ---------------------------------------------------------------------------------------

    /// <summary>The first click on «Ripristina predefiniti» asks for a confirmation.</summary>
    [ObservableProperty]
    private bool _confirmingReset;

    [RelayCommand]
    private void AskReset() => ConfirmingReset = true;

    [RelayCommand]
    private void CancelReset() => ConfirmingReset = false;

    /// <summary>Every option on this page back to its default, except starting with Windows (an installation choice).</summary>
    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        ConfirmingReset = false;
        var defaults = new UiSettings();
        OverlayEnabled = defaults.OverlayEnabled;
        PlaySound = defaults.PlaySoundForCritical;
        SuppressWhenPresenting = defaults.SuppressWhenPresenting;
        SuppressWhenOnVpn = defaults.SuppressWhenOnVpn;
        Screen = Screens[0];
        OverlayBackground = defaults.OverlayBackground;
        OverlayTextColor = defaults.OverlayTextColor;
        OverlayAccent = defaults.OverlayAccent;
        _settings.OverlayOffsetX = defaults.OverlayOffsetX;
        _settings.OverlayTop = defaults.OverlayTop;
        _settings.Save();
        _settingsChanged();

        if (PollAvailable && Service() is { } api)
        {
            try
            {
                ShowPoll(await api.SetPollSecondsAsync(0));
            }
            catch (Exception ex) when (IsServiceFailure(ex))
            {
                PollNote = $"Impossibile ripristinare l'intervallo: {ex.Message}";
            }
        }

        Note = "Opzioni riportate ai valori predefiniti (l'avvio con Windows non è stato cambiato).";
    }
}
