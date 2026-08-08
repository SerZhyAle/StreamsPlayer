namespace StreamsPlayer.Core;

/// <summary>
/// SP-0070: how far the media currently open has got, as monotonic totals the player can difference.
/// <para><see cref="DisplayedPictures"/> is what actually reached the screen, which is the only counter
/// a "the picture is frozen" report can be checked against. <see cref="InputBytes"/> is what the demuxer
/// has read, not what the access layer reports: on HLS and DASH the segments are fetched below the access
/// layer, so the access-side byte count stays frozen for a whole healthy session.</para>
/// </summary>
public readonly record struct PlaybackProgressCounters(long DisplayedPictures, long InputBytes);

/// <summary>
/// SP-0070: the whole "is this stream frozen" decision. The App owns the clock, the timer and the media
/// engine; this type only answers the question from the observations the player already makes, so the
/// behaviour is decidable - and testable - without a window, a network, or a media backend.
///
/// <para>The rule the owner chose is deliberately conservative: a freeze is <em>both</em> signals at
/// once - nothing new reaches the screen <em>and</em> nothing new arrives from the source. Either one
/// alone is ordinary. A stream that is rebuffering keeps receiving bytes; a stream whose decoder is
/// briefly behind keeps displaying pictures. Only when both stop for <see cref="FreezeAfter"/> is the
/// user looking at a still image with nothing on the way to replace it.</para>
///
/// <para>Two fallbacks matter as much as the rule. Until displayed pictures have grown at least once on
/// this leg there is no picture signal to lose - an audio-only stream, or the seconds before the first
/// frame - and until then the detector judges by media time, which is what the player did before this
/// type existed. A backend that reports no counters at all falls back the same way: null is "this engine
/// has no telemetry", never "this engine is fine", and it must not silently disarm the watchdog.</para>
///
/// <para>Time is a parameter on every call, never read ambiently. The caller supplies a monotonic
/// reading (a session stopwatch), so a system clock change cannot make the threshold expire early or
/// hang forever. Feed it from a clock that spans the window rather than one that restarts per reconnect
/// leg, or the interval would be no interval at all.</para>
///
/// <para>The type is not thread-safe: the player feeds it from the UI thread only.</para>
/// </summary>
public sealed class PlaybackFreezeDetector
{
    /// <summary>
    /// How long every observed kind of progress must be absent before the stream counts as frozen.
    /// <para>Inherited from the watchdog this replaces (three polls of three seconds) and kept there on
    /// purpose: it is long enough that an ordinary live rebuffer finishes inside it, and short enough
    /// that the user has not yet given up on a picture that stopped moving. Expressed as a duration
    /// rather than a poll count so the answer no longer depends on how often the caller ticks.</para>
    /// </summary>
    public static readonly TimeSpan FreezeAfter = TimeSpan.FromSeconds(9);

    /// <summary>
    /// How far media time must advance between two observations to count as progress in the fallback
    /// test. Carried over unchanged from the watchdog this replaces.
    /// </summary>
    public const long PositionProgressMilliseconds = 500;

    private PlaybackProgressCounters? _baseline;
    private long _lastPosition;
    private bool _everDisplayed;
    private TimeSpan? _lastProgress;

    /// <summary>
    /// Forgets everything about the current leg. The caller invokes this per media open: a new media
    /// restarts the engine's counters from zero, and differencing across that boundary would invent
    /// either a freeze or a burst of progress that never happened.
    /// </summary>
    public void Reset()
    {
        _baseline = null;
        _lastPosition = 0;
        _everDisplayed = false;
        _lastProgress = null;
    }

    /// <summary>
    /// One observation, at whatever cadence the caller already polls the engine.
    /// </summary>
    /// <param name="now">A monotonic reading spanning the whole session.</param>
    /// <param name="isPlaying">Whether the engine reports an active playing state.</param>
    /// <param name="positionMs">Media position, or a negative value when unknown.</param>
    /// <param name="counters">The engine's progress totals, or null where it reports none.</param>
    /// <returns>
    /// True exactly once per detected freeze, at which point the window restarts. Reporting once is what
    /// lets the caller start recovery without the next tick firing a second one on the same freeze.
    /// </returns>
    public bool Observe(TimeSpan now, bool isPlaying, long positionMs, PlaybackProgressCounters? counters)
    {
        if (!isPlaying)
        {
            // Not an assertion of health - an engine that is not playing is simply not a stream that
            // stopped moving, and tearing one down would fight whatever paused or stopped it.
            RememberProgress(now, positionMs, counters);
            return false;
        }

        if (HasProgressed(positionMs, counters))
        {
            RememberProgress(now, positionMs, counters);
            return false;
        }

        UpdateBaselines(positionMs, counters);
        if (_lastProgress is not { } since)
        {
            // First observation of the leg: there is nothing to difference yet, so start the window
            // here rather than treating an unknown past as nine seconds of silence.
            _lastProgress = now;
            return false;
        }

        if (now - since < FreezeAfter)
        {
            return false;
        }

        _lastProgress = now;
        return true;
    }

    /// <summary>
    /// Whether anything moved since the previous observation. Uses the picture/data pair once this leg
    /// has proven it has a picture at all, and media time otherwise (see the type's remarks).
    /// </summary>
    private bool HasProgressed(long positionMs, PlaybackProgressCounters? counters)
    {
        if (counters is { } current && _baseline is { } previous && _everDisplayed)
        {
            if (current.DisplayedPictures < previous.DisplayedPictures || current.InputBytes < previous.InputBytes)
            {
                // The engine restarted its media underneath us and the totals went back to zero. That is
                // not a frozen stream; the next observation measures against the new baseline.
                return true;
            }

            return current.DisplayedPictures > previous.DisplayedPictures || current.InputBytes > previous.InputBytes;
        }

        return positionMs >= 0 && positionMs - _lastPosition >= PositionProgressMilliseconds;
    }

    private void RememberProgress(TimeSpan now, long positionMs, PlaybackProgressCounters? counters)
    {
        _lastProgress = now;
        UpdateBaselines(positionMs, counters);
    }

    /// <summary>
    /// Advances the baselines this observation is measured against, and latches the fact that this leg
    /// has a picture at all.
    /// </summary>
    private void UpdateBaselines(long positionMs, PlaybackProgressCounters? counters)
    {
        if (positionMs >= 0)
        {
            _lastPosition = positionMs;
        }

        if (counters is not { } current)
        {
            return;
        }

        if (_baseline is { } previous && current.DisplayedPictures > previous.DisplayedPictures)
        {
            // The latch is what makes the picture signal safe on a stream that has no picture: it can
            // only ever be set by pictures that were actually displayed on this leg.
            _everDisplayed = true;
        }

        _baseline = current;
    }
}
