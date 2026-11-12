namespace LiveHelpDesk.App.Services;

/// <summary>
/// Pauses the alerts for as long as the screen is shared, and lets the user override that on purpose. Each time a
/// share starts the alerts are paused again; an explicit, confirmed "reactivate" keeps them on until that share ends.
/// It never touches the saved on/off choice: when the share ends everything is back as the user left it.
/// </summary>
public sealed class ScreenShareGuard(UiSettings settings, IDoNotDisturb screenShare)
{
    private readonly object _gate = new();
    private bool _sharing;
    private bool _confirmed;

    /// <summary>Raised (possibly off the UI thread) when the share starts or ends, or the user answers.</summary>
    public event Action? Changed;

    /// <summary>The screen is being shared (and the user wants alerts held back while it is).</summary>
    public bool IsSharing
    {
        get
        {
            Update();
            lock (_gate)
            {
                return _sharing;
            }
        }
    }

    /// <summary>Sharing, and the user has not reactivated the alerts for this share: nothing may show on screen.</summary>
    public bool IsPaused
    {
        get
        {
            Update();
            lock (_gate)
            {
                return _sharing && !_confirmed;
            }
        }
    }

    /// <summary>Sharing, but the user explicitly reactivated the alerts for this share.</summary>
    public bool IsReactivated
    {
        get
        {
            Update();
            lock (_gate)
            {
                return _sharing && _confirmed;
            }
        }
    }

    /// <summary>Looks at the screen now. A new share starts paused again; the end of a share forgets any confirmation.</summary>
    public void Update()
    {
        var changed = false;
        lock (_gate)
        {
            var sharing = settings.SuppressWhenPresenting && screenShare.IsBusy();
            if (sharing != _sharing)
            {
                _sharing = sharing;
                _confirmed = false;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The user confirmed: alerts stay on until this share ends.</summary>
    public void Reactivate()
    {
        lock (_gate)
        {
            if (!_sharing || _confirmed)
            {
                return;
            }

            _confirmed = true;
        }

        Changed?.Invoke();
    }

    /// <summary>The user paused the alerts again during a share they had reactivated them in.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_confirmed)
            {
                return;
            }

            _confirmed = false;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// What the "Avvisi" button does when pressed. While the share has the alerts paused it asks first; once the user
    /// has reactivated them it pauses them again; otherwise it flips the saved choice.
    /// </summary>
    /// <returns>The new saved on/off value, or null when it must stay as it is.</returns>
    public async Task<bool?> ToggleAsync(bool alertsEnabled, Func<Task<bool>> confirmReactivate)
    {
        if (IsPaused)
        {
            if (!await confirmReactivate())
            {
                return null;
            }

            Reactivate();
            return true;
        }

        if (IsReactivated)
        {
            Pause();
            return null;
        }

        return !alertsEnabled;
    }
}
