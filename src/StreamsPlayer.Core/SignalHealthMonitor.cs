namespace StreamsPlayer.Core;

/// <summary>
/// SP-0045: the whole green/yellow/red decision behind the player's stripe. The App owns the clock,
/// the colours and the media engine; this type only answers "what is the state now" from the sequence
/// of things the player already observes, so the behaviour is decidable - and testable - without a
/// window, a network, or a media backend.
///
/// <para>Time is a parameter on every call, never read ambiently. The caller supplies a monotonic
/// reading (a session stopwatch), so a system clock change cannot make an interval expire early or
/// hang forever.</para>
///
/// <para>The type is not thread-safe: the player feeds it from the UI thread only.</para>
/// </summary>
public sealed class SignalHealthMonitor
{
    /// <summary>
    /// How long a stream must go undisturbed before green is restored (decision 6).
    /// Derived from the anti-flicker constraint, not chosen for comfort: a stream that dips once a
    /// minute has to read as steadily yellow, and any shorter interval lets it strobe green between
    /// dips - which is exactly the bar the user learns to ignore.
    /// </summary>
    public static readonly TimeSpan CleanInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How much the loss counters may grow within one observation sample before it counts as trouble
    /// (decision 7). Summed across all three counters: they describe one thing to the user - the
    /// picture is breaking up - and decision 1 gives them one colour, so they get one number.
    /// <para>At the player's two-second sampling cadence this is roughly two and a half lost pictures
    /// per second, which is visible; the handful of losses a stream drops at startup or on a
    /// resolution switch stays below it and stays green.</para>
    /// </summary>
    public const long LossThreshold = 5;

    private DecoderLossCounters? _lossBaseline;
    private TimeSpan? _lastDisturbance;
    private bool _everLive;
    private bool _lost;

    /// <summary>
    /// A new media is being opened (first attempt, reconnect, or a manual retry). Drops the counter
    /// baseline: the next media starts its totals from zero, and differencing across that boundary
    /// would invent a disturbance. Deliberately does not change the state - an opening stream is
    /// neither healthy nor broken, and a reconnect must stay red while it is in progress.
    /// </summary>
    public void NotifyOpening()
    {
        _lossBaseline = null;
    }

    /// <summary>
    /// The stream reached the live edge. Leaves the lost state, but does not clear the disturbance
    /// window: the interruption that caused the reconnect was real and recent, so the stripe passes
    /// through yellow and earns green one clean interval later (decision 8). A first connect that was
    /// never disturbed has no window to wait out and goes straight to green.
    /// </summary>
    public void NotifyLive()
    {
        _everLive = true;
        _lost = false;
    }

    /// <summary>
    /// Something disturbed a playing stream: a stall, a rebuffer, a watchdog-detected freeze, or
    /// enough decoder loss. Safe and intended to call repeatedly while the disturbance lasts - each
    /// call restarts the clean interval, so a long stall is one continuous yellow rather than a bar
    /// that goes green sixty seconds into it.
    /// </summary>
    public void NotifyDisturbance(TimeSpan now) => _lastDisturbance = now;

    /// <summary>
    /// The player lost the stream and started recovering. Red only if the stream had reached live at
    /// least once (decision 2): a first connect that retries is still just connecting, and flashing
    /// red at it would make red mean nothing. The interruption is recorded either way, so the stream
    /// still has to earn green back once it does play.
    /// </summary>
    public void NotifyRecovering(TimeSpan now)
    {
        _lastDisturbance = now;
        if (_everLive)
        {
            _lost = true;
        }
    }

    /// <summary>
    /// The channel failed terminally. Red unconditionally, including on a first connect that never
    /// played: at that point the outcome is decided, which is precisely what red means.
    /// </summary>
    public void NotifyFailed(TimeSpan now)
    {
        _lastDisturbance = now;
        _lost = true;
    }

    /// <summary>
    /// One observation of the decoder's loss counters, at the player's existing sampling cadence.
    /// <para>A null reading is not evidence of trouble - it is a backend that reports no counters at
    /// all - so it is ignored rather than assumed bad. Without this, the engine with less telemetry
    /// would sit permanently yellow while the better-instrumented one showed green.</para>
    /// <para>Deltas are clamped at zero, so a counter that restarted (a new media, an engine reset)
    /// cannot fabricate a disturbance; the first sample after an open only establishes a baseline.</para>
    /// </summary>
    public void NotifySample(TimeSpan now, DecoderLossCounters? counters)
    {
        if (counters is not { } current)
        {
            return;
        }

        if (_lossBaseline is not { } previous)
        {
            _lossBaseline = current;
            return;
        }

        _lossBaseline = current;
        var growth =
            Delta(current.LostPictures, previous.LostPictures) +
            Delta(current.Corrupted, previous.Corrupted) +
            Delta(current.Discontinuities, previous.Discontinuities);

        if (growth >= LossThreshold)
        {
            NotifyDisturbance(now);
        }
    }

    /// <summary>The state to show at <paramref name="now"/>.</summary>
    public SignalHealth Evaluate(TimeSpan now)
    {
        if (_lost)
        {
            return SignalHealth.Lost;
        }

        if (!_everLive)
        {
            return SignalHealth.Unknown;
        }

        return _lastDisturbance is { } disturbed && now - disturbed < CleanInterval
            ? SignalHealth.Degraded
            : SignalHealth.Good;
    }

    private static long Delta(long current, long previous) => current > previous ? current - previous : 0;
}
