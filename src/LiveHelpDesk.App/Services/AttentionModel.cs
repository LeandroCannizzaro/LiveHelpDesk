using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.App.Services;

public enum AttentionLevel
{
    None,
    Attention,
    Critical,
}

/// <summary>Something that needs a person to look at it.</summary>
public sealed record AttentionItem(string EventKey, string Title, string Body, Severity Severity, Uri? Url, DateTimeOffset ReceivedAt);

/// <summary>
/// What is waiting for the user's attention: drives the overlay and the tray icon. Pure state, no UI, so the
/// rules (what blinks, what snoozing hides, what breaks through a snooze) can be tested exactly.
/// </summary>
public sealed class AttentionModel
{
    private const int MaxItems = 50;

    private readonly List<AttentionItem> _items = [];
    private readonly Dictionary<string, DateTimeOffset> _addedAt = [];
    private DateTimeOffset? _snoozedUntil;
    private DateTimeOffset? _answeredAt;

    public event EventHandler? Changed;

    public IReadOnlyList<AttentionItem> Items => [.. _items];

    public int Count => _items.Count;

    public DateTimeOffset? SnoozedUntil => _snoozedUntil;

    /// <summary>The most urgent waiting item, newest first among equals.</summary>
    public AttentionItem? Headline =>
        _items.OrderByDescending(i => i.Severity).ThenByDescending(i => i.ReceivedAt).FirstOrDefault();

    /// <summary>How urgent things are, regardless of snoozing: this is what the tray icon shows.</summary>
    public AttentionLevel Level => _items.Count == 0
        ? AttentionLevel.None
        : _items.Any(i => i.Severity == Severity.Critical) ? AttentionLevel.Critical : AttentionLevel.Attention;

    /// <summary>What the overlay shows: nothing while snoozed.</summary>
    public AttentionLevel VisibleLevel(DateTimeOffset now) => IsSnoozed(now) ? AttentionLevel.None : Level;

    public bool IsSnoozed(DateTimeOffset now) => _snoozedUntil is { } until && now < until;

    /// <summary>
    /// How long the overlay has been asking with nobody answering: since the oldest waiting item arrived, the user
    /// last opened, saw or snoozed something, or the snooze ran out, whichever is latest. Zero when nothing waits or while snoozed.
    /// </summary>
    public TimeSpan UnansweredFor(DateTimeOffset now)
    {
        if (_items.Count == 0 || IsSnoozed(now))
        {
            return TimeSpan.Zero;
        }

        var since = _items.Min(i => _addedAt.GetValueOrDefault(i.EventKey, now));
        if (_answeredAt is { } answered && answered > since)
        {
            since = answered;
        }

        if (_snoozedUntil is { } snoozeEnd && snoozeEnd > since)
        {
            since = snoozeEnd;
        }

        return now > since ? now - since : TimeSpan.Zero;
    }

    /// <summary>False when the same event is already waiting. A critical item always ends a snooze.</summary>
    public bool Add(AttentionItem item, DateTimeOffset now)
    {
        if (_items.Any(i => i.EventKey == item.EventKey))
        {
            return false;
        }

        _items.Add(item);
        _addedAt[item.EventKey] = now;
        if (_items.Count > MaxItems)
        {
            // Keep the urgent ones: drop the oldest of the least urgent.
            var dropped = _items.OrderBy(i => i.Severity).ThenBy(i => i.ReceivedAt).First();
            _items.Remove(dropped);
            _addedAt.Remove(dropped.EventKey);
        }

        if (item.Severity == Severity.Critical)
        {
            _snoozedUntil = null;
        }

        Raise();
        return true;
    }

    /// <param name="now">When the user answered; restarts the overlay's escalation for what is still waiting.</param>
    public AttentionItem? Acknowledge(string eventKey, DateTimeOffset? now = null)
    {
        var item = _items.FirstOrDefault(i => i.EventKey == eventKey);
        if (item is null)
        {
            return null;
        }

        _items.Remove(item);
        _addedAt.Remove(eventKey);
        _answeredAt = now ?? DateTimeOffset.UtcNow;
        Raise();
        return item;
    }

    public IReadOnlyList<AttentionItem> AcknowledgeAll(DateTimeOffset? now = null)
    {
        var all = Items;
        if (all.Count > 0)
        {
            _items.Clear();
            _addedAt.Clear();
            _answeredAt = now ?? DateTimeOffset.UtcNow;
            Raise();
        }

        return all;
    }

    public void Snooze(TimeSpan duration, DateTimeOffset now)
    {
        _snoozedUntil = now + duration;
        _answeredAt = now;
        Raise();
    }

    public void EndSnooze()
    {
        if (_snoozedUntil is not null)
        {
            _snoozedUntil = null;
            Raise();
        }
    }

    /// <summary>Lets the owner re-evaluate time-based state (a snooze that has just run out).</summary>
    public void Tick() => Raise();

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
