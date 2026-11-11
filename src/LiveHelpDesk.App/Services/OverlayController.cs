using Avalonia;
using Avalonia.Threading;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.App.Views;

namespace LiveHelpDesk.App.Services;

/// <summary>
/// Decides when the floating pill is on screen and how it blinks. It follows the <see cref="AttentionModel"/>: nothing
/// waiting, snoozed, switched off, or the user busy with a full-screen app, and it stays hidden.
/// </summary>
public sealed class OverlayController
{
    private const double OverlayWidth = 720;
    private const int TickMilliseconds = 200;

    private readonly AttentionModel _model;
    private readonly UiSettings _settings;
    private readonly IDoNotDisturb _doNotDisturb;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action _playAlertSound;
    private readonly OverlayViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private readonly List<OverlayWindow> _windows = [];
    private bool _wasCritical;

    public OverlayController(
        AttentionModel model,
        UiSettings settings,
        IDoNotDisturb doNotDisturb,
        Func<DateTimeOffset> clock,
        OverlayViewModel viewModel,
        Action playAlertSound)
    {
        _model = model;
        _settings = settings;
        _doNotDisturb = doNotDisturb;
        _clock = clock;
        _viewModel = viewModel;
        _playAlertSound = playAlertSound;
        _viewModel.UseTheme(settings);

        _model.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(TickMilliseconds), DispatcherPriority.Background, (_, _) => OnTick());
    }

    public bool IsShown => _windows.Any(w => w.IsVisible);

    /// <summary>How many pills are on screen: one, or one per screen with "Tutti gli schermi".</summary>
    public int ShownCount => _windows.Count(w => w.IsVisible);

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        foreach (var window in _windows)
        {
            window.Close();
        }

        _windows.Clear();
    }

    /// <summary>Re-evaluates what should be on screen right now.</summary>
    public void Refresh()
    {
        var now = _clock();
        _viewModel.Update(_model, now);

        var shouldShow = _settings.OverlayEnabled && _model.VisibleLevel(now) != AttentionLevel.None && !_doNotDisturb.IsBusy();
        if (shouldShow)
        {
            Show();
            var critical = _model.VisibleLevel(now) == AttentionLevel.Critical;
            if (critical && !_wasCritical && _settings.PlaySoundForCritical)
            {
                _playAlertSound();
            }

            _wasCritical = critical;
        }
        else
        {
            // The pills share one view model: "Vista", "15 min" or "Apri" on any of them empties the model and hides them all.
            _wasCritical = false;
            foreach (var window in _windows)
            {
                window.Hide();
            }
        }
    }

    private void OnTick()
    {
        if (!IsShown && _model.Count == 0)
        {
            return;
        }

        // A snooze may have just run out, or the user may have left the presentation; the heartbeat itself is a looping animation in the view.
        Refresh();
    }

    private void Show()
    {
        var targets = TargetScreens(Window(0));
        for (var i = 0; i < Math.Max(1, targets.Count); i++)
        {
            var window = Window(i);
            if (!window.IsVisible)
            {
                Place(window, targets.ElementAtOrDefault(i));
                window.Show();
                window.KeepOffTheKeyboard();
            }
        }

        // Fewer screens than before (a monitor unplugged, or back to a single screen): the extra pills go.
        while (_windows.Count > Math.Max(1, targets.Count))
        {
            _windows[^1].Close();
            _windows.RemoveAt(_windows.Count - 1);
        }
    }

    private OverlayWindow Window(int index)
    {
        while (_windows.Count <= index)
        {
            var window = new OverlayWindow { DataContext = _viewModel };
            window.Moved += (_, _) => RememberPosition(window);
            _windows.Add(window);
        }

        return _windows[index];
    }

    private void Place(OverlayWindow window, Avalonia.Platform.Screen? screen)
    {
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var x = area.X + (area.Width - OverlayWidth * scale) / 2 + _settings.OverlayOffsetX * scale;
        window.Position = new PixelPoint((int)x, (int)(area.Y + _settings.OverlayTop * scale));
    }

    /// <summary>
    /// Every screen with "Tutti gli schermi"; otherwise the one the user picked, or the main one when that screen
    /// is gone (a laptop undocked).
    /// </summary>
    private IReadOnlyList<Avalonia.Platform.Screen> TargetScreens(OverlayWindow window)
    {
        var all = window.Screens.All;
        if (_settings.OverlayScreen == UiSettings.AllScreens)
        {
            return all;
        }

        var index = _settings.OverlayScreen - 1;
        var chosen = index >= 0 && index < all.Count ? all[index] : window.Screens.Primary ?? all.FirstOrDefault();
        return chosen is null ? [] : [chosen];
    }

    /// <summary>Settings changed (screen, position, on/off): apply them now.</summary>
    public void Reposition()
    {
        if (IsShown)
        {
            var targets = TargetScreens(_windows[0]);
            for (var i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].IsVisible)
                {
                    Place(_windows[i], targets.ElementAtOrDefault(i));
                }
            }
        }

        // Showing again also adds or removes pills when "Tutti gli schermi" was switched on or off.
        Refresh();
    }

    private void RememberPosition(OverlayWindow window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? TargetScreens(window).FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var centred = area.X + (area.Width - OverlayWidth * scale) / 2;
        _settings.OverlayOffsetX = (window.Position.X - centred) / scale;
        _settings.OverlayTop = Math.Max(0, (window.Position.Y - area.Y) / scale);
        _settings.Save();

        // With a pill on every screen, the others follow to the same spot on theirs.
        var targets = TargetScreens(window);
        for (var i = 0; i < _windows.Count; i++)
        {
            if (_windows[i] != window && _windows[i].IsVisible)
            {
                Place(_windows[i], targets.ElementAtOrDefault(i));
            }
        }
    }
}
