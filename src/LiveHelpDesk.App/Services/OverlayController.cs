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
    private OverlayWindow? _window;
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

    public bool IsShown => _window?.IsVisible == true;

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        _window?.Close();
        _window = null;
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
            _wasCritical = false;
            _window?.Hide();
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
        if (_window is null)
        {
            _window = new OverlayWindow { DataContext = _viewModel };
            _window.Moved += (_, _) => RememberPosition();
        }

        if (!_window.IsVisible)
        {
            Place(_window);
            _window.Show();
            _window.KeepOffTheKeyboard();
        }
    }

    private void Place(OverlayWindow window)
    {
        var screen = ChooseScreen(window);
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var x = area.X + (area.Width - OverlayWidth * scale) / 2 + _settings.OverlayOffsetX * scale;
        window.Position = new PixelPoint((int)x, (int)(area.Y + _settings.OverlayTop * scale));
    }

    /// <summary>The screen the user picked, or the main one when that screen is gone (a laptop undocked).</summary>
    private Avalonia.Platform.Screen? ChooseScreen(OverlayWindow window)
    {
        var all = window.Screens.All;
        var index = _settings.OverlayScreen - 1;
        return index >= 0 && index < all.Count ? all[index] : window.Screens.Primary ?? all.FirstOrDefault();
    }

    /// <summary>Settings changed (screen, position, on/off): apply them now.</summary>
    public void Reposition()
    {
        if (_window is { IsVisible: true })
        {
            Place(_window);
        }

        Refresh();
    }

    private void RememberPosition()
    {
        if (_window is null)
        {
            return;
        }

        var screen = ChooseScreen(_window);
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var centred = area.X + (area.Width - OverlayWidth * scale) / 2;
        _settings.OverlayOffsetX = (_window.Position.X - centred) / scale;
        _settings.OverlayTop = Math.Max(0, (_window.Position.Y - area.Y) / scale);
        _settings.Save();
    }
}
