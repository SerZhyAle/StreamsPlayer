namespace StreamsPlayer.Core;

/// <summary>
/// SP-0169: the decision "this radio connection is open but has stopped delivering". The App owns the clock, the
/// timer and the media engine; this type only answers the question from one monotonic total the engine already
/// keeps - the bytes read from the source - so the rule is decidable without a window, a network or a backend.
///
/// <para>One signal, on purpose. A live radio stream is a steady flow of bytes, and what a silent stall looks like
/// from outside is that flow stopping while the connection stays open and raises no error and no end. Played
/// buffers are not used: they stop growing as soon as the playback buffer drains, which is a consequence of the
/// stall and would make this fire later, not sooner.</para>
///
/// <para>Time is a parameter on every call, never read ambiently: the caller supplies a monotonic reading, so a
/// system clock change cannot make the bound expire early or hang forever. The type is not thread-safe; the
/// caller feeds it from the UI thread only, one instance per connection.</para>
/// </summary>
public sealed class AudioStallDetector
{
    /// <summary>
    /// How long the source may deliver nothing before the connection counts as stalled. Long enough that a
    /// server pausing to rebuffer or a Wi-Fi roam finishes inside it, short enough that the listener has not
    /// yet concluded the radio is dead. Shared with nobody: the video watchdog has its own (SP-0070).
    /// </summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    private long _lastBytes = -1;
    private TimeSpan _lastProgress;

    /// <summary>
    /// One observation of the bytes read so far on this connection.
    /// </summary>
    /// <returns>
    /// True exactly once per detected stall, at which point the window restarts - so a caller that keeps
    /// observing while it recovers does not raise a second stall for the same silence.
    /// </returns>
    public bool Observe(TimeSpan now, long bytesRead)
    {
        if (_lastBytes < 0 || bytesRead != _lastBytes)
        {
            // The first observation only establishes the baseline. A total that moved in either direction is
            // not a stall: growth is data, and a drop is the engine restarting its counters underneath us.
            _lastBytes = bytesRead;
            _lastProgress = now;
            return false;
        }

        if (now - _lastProgress < StallAfter)
        {
            return false;
        }

        _lastProgress = now;
        return true;
    }
}
