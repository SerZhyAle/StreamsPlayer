namespace StreamsPlayer.Core;

/// <summary>
/// Counts the native engines a feature had to abandon to a stop that did not return, and pauses the feature for
/// the rest of the session once too many are outstanding (SP-0166).
/// </summary>
/// <remarks>
/// An abandoned engine keeps its threads and any pinned buffer until the hung stop finally comes back, and on a
/// network where stops hang that is every capture. SP-0120 accepted one such engine until exit; this is what
/// bounds the total. The pause latches: a stop that returns late frees its engine and lowers the count, but does
/// not start the feature again, because a network that hung a stop once is the network that will hang the next.
/// Thread-safe; every member may be called from any thread.
/// </remarks>
public sealed class AbandonedEngineBudget
{
    /// <summary>How many engines may be outstanding before the feature pauses.</summary>
    public const int DefaultCap = 3;

    private readonly object _gate = new();
    private readonly int _cap;
    private int _outstanding;
    private bool _paused;

    public AbandonedEngineBudget(int cap = DefaultCap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        _cap = cap;
    }

    /// <summary>True once the cap was reached; stays true for the life of this instance.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>Engines abandoned and not yet released.</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate)
            {
                return _outstanding;
            }
        }
    }

    /// <summary>
    /// Records one more abandoned engine. Returns <c>true</c> exactly once: on the call that reaches the cap and
    /// so pauses the feature - the caller logs the pause then.
    /// </summary>
    public bool RecordAbandoned()
    {
        lock (_gate)
        {
            _outstanding++;
            if (_paused || _outstanding < _cap)
            {
                return false;
            }

            _paused = true;
            return true;
        }
    }

    /// <summary>Records that an abandoned engine was finally released.</summary>
    public void RecordReleased()
    {
        lock (_gate)
        {
            if (_outstanding > 0)
            {
                _outstanding--;
            }
        }
    }
}
