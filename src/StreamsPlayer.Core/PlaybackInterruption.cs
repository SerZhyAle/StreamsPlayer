namespace StreamsPlayer.Core;

/// <summary>SP-0072: why the picture is not on screen, in the terms a viewer can act on.</summary>
/// <remarks>
/// Internal distinctions that change nothing for the viewer are deliberately absent: a stall, a
/// watchdog-caught freeze and a rebuffer are all <see cref="SignalLost"/>, because from the sofa they
/// are one event - the picture stopped and the player is looking for it.
/// </remarks>
public enum PlaybackInterruptionKind
{
    /// <summary>The picture is on screen; nothing is to be said.</summary>
    None,

    /// <summary>Nothing has played yet on this leg: the first open, or a manual retry.</summary>
    Connecting,

    /// <summary>A stream that was playing ran out of picture and the player is looking for more.</summary>
    SignalLost,

    /// <summary>The bounded recovery is re-opening the stream, on a numbered attempt.</summary>
    Reconnecting,

    /// <summary>The adaptive ceiling moved, so the media is being re-opened at another rendition.</summary>
    SwitchingQuality,

    /// <summary>The channel failed terminally. The screen stays black, so it still gets a word.</summary>
    Unavailable
}

/// <summary>
/// What to show at a given moment. <see cref="Kind"/> is <see cref="PlaybackInterruptionKind.None"/>
/// whenever nothing should be on screen, which includes a blackout too short to be worth naming.
/// </summary>
/// <param name="Attempt">Which recovery attempt is running; meaningful only for
/// <see cref="PlaybackInterruptionKind.Reconnecting"/>.</param>
/// <param name="Budget">How many attempts that recovery is allowed.</param>
public readonly record struct PlaybackInterruptionNotice(
    PlaybackInterruptionKind Kind,
    int Attempt,
    int Budget)
{
    /// <summary>Nothing to show.</summary>
    public static PlaybackInterruptionNotice Silent => new(PlaybackInterruptionKind.None, 0, 0);
}

/// <summary>
/// SP-0072: the whole rule behind the line of text drawn over a black player. The App owns the clock,
/// the media engine and the overlay; this type only answers "what, if anything, does the screen say
/// now" from events the player already raises - so the behaviour is decidable, and testable, without a
/// window, a network or a media backend.
///
/// <para>Time is a parameter on every call, never read ambiently. The caller supplies a monotonic
/// reading (the session stopwatch), so a system clock change cannot make the delay expire early or
/// hang forever.</para>
///
/// <para>The type is not thread-safe: the player feeds it from the UI thread only.</para>
/// </summary>
public sealed class PlaybackInterruptionTracker
{
    /// <summary>
    /// How long the picture must be missing before the screen says anything.
    /// <para>This single constant is the whole anti-flicker rule (SP-0072 risk 1). Below it a gap is a
    /// blip the eye barely registers and a caption would be a flash of its own; above it the screen has
    /// been visibly black, and the measured gaps on a breaking source are 3 to 18 seconds. There is
    /// deliberately no minimum <em>visible</em> time to pair with it: holding the caption over a picture
    /// that has already come back is what acceptance criterion 2 forbids in as many words.</para>
    /// </summary>
    public static readonly TimeSpan AppearDelay = TimeSpan.FromSeconds(1);

    private PlaybackInterruptionKind _kind = PlaybackInterruptionKind.None;
    private TimeSpan _since;
    private int _attempt;
    private int _budget;

    /// <summary>
    /// The picture is gone, for the given reason. Safe and intended to call repeatedly: a recovery that
    /// runs probe -&gt; attempt 1 -&gt; attempt 2 is one blackout, so the reason is replaced in place and the
    /// delay keeps running from where the blackout began. Restarting it on every cause change is what
    /// would make the caption vanish and return in the middle of a single interruption.
    /// </summary>
    public void NotifyInterrupted(TimeSpan now, PlaybackInterruptionKind kind, int attempt = 0, int budget = 0)
    {
        ArgumentOutOfRangeException.ThrowIfEqual((int)kind, (int)PlaybackInterruptionKind.None);

        if (_kind == PlaybackInterruptionKind.None)
        {
            _since = now;
        }

        _kind = kind;
        _attempt = attempt;
        _budget = budget;
    }

    /// <summary>The picture is back. Clears the caption in the same instant, with no residual hold.</summary>
    public void NotifyLive()
    {
        _kind = PlaybackInterruptionKind.None;
        _attempt = 0;
        _budget = 0;
    }

    /// <summary>What the screen should say at <paramref name="now"/>.</summary>
    public PlaybackInterruptionNotice Evaluate(TimeSpan now) =>
        _kind != PlaybackInterruptionKind.None && now - _since >= AppearDelay
            ? new PlaybackInterruptionNotice(_kind, _attempt, _budget)
            : PlaybackInterruptionNotice.Silent;
}
