namespace LiveHelpDesk.Core.Delivery;

public enum BreakerState
{
    /// <summary>Working: send normally.</summary>
    Closed,

    /// <summary>Failing: stop hammering it until the cool-down is over.</summary>
    Open,

    /// <summary>Cool-down over: one attempt decides whether to close again.</summary>
    HalfOpen,
}

public enum BreakerChange
{
    None,
    Opened,
    Recovered,
}

/// <summary>
/// Stops a failing channel from being tried over and over (and from burning every message's attempts while the
/// provider is simply down). Time is passed in, so behaviour is exact in tests.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _maxCooldown;
    private int _failures;
    private DateTimeOffset _openedAt;
    private TimeSpan _currentCooldown;
    private bool _probing;

    public CircuitBreaker(int threshold, TimeSpan cooldown, TimeSpan maxCooldown)
    {
        _threshold = threshold;
        _cooldown = cooldown;
        _maxCooldown = maxCooldown;
        _currentCooldown = cooldown;
    }

    public BreakerState State { get; private set; } = BreakerState.Closed;

    public bool Allow(DateTimeOffset now)
    {
        switch (State)
        {
            case BreakerState.Closed:
                return true;

            case BreakerState.Open when now - _openedAt >= _currentCooldown:
                State = BreakerState.HalfOpen;
                _probing = true;
                return true;

            case BreakerState.HalfOpen when !_probing:
                _probing = true;
                return true;

            default:
                return false;
        }
    }

    public BreakerChange RecordSuccess()
    {
        var recovered = State != BreakerState.Closed;
        State = BreakerState.Closed;
        _failures = 0;
        _probing = false;
        _currentCooldown = _cooldown;
        return recovered ? BreakerChange.Recovered : BreakerChange.None;
    }

    public BreakerChange RecordFailure(DateTimeOffset now)
    {
        _failures++;
        _probing = false;

        if (State == BreakerState.HalfOpen)
        {
            // The probe failed: back off for longer.
            _currentCooldown = TimeSpan.FromTicks(Math.Min(_currentCooldown.Ticks * 2, _maxCooldown.Ticks));
            State = BreakerState.Open;
            _openedAt = now;
            return BreakerChange.None;
        }

        if (State == BreakerState.Closed && _failures >= _threshold)
        {
            State = BreakerState.Open;
            _openedAt = now;
            return BreakerChange.Opened;
        }

        return BreakerChange.None;
    }
}
