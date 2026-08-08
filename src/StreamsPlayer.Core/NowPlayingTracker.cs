namespace StreamsPlayer.Core;

/// <summary>
/// SP-0073: the whole rule behind the player's "what is on air" line. The App owns the clock, the media
/// engine and the panel; this type only answers "which words should be under the channel name now" from
/// readings the player already takes - so the behaviour is decidable, and testable, without a window, a
/// network or a media backend.
///
/// <para>Time is a parameter on every call, never read ambiently. The caller supplies a monotonic
/// reading (the session stopwatch), so a system clock change cannot freeze the line or release it
/// early.</para>
///
/// <para>The type is not thread-safe: the player feeds it from the UI thread only.</para>
/// </summary>
public sealed class NowPlayingTracker
{
    /// <summary>
    /// Upper bound on the text this line will carry. Shorter than an ICY block is allowed to be, because
    /// this is one line inside a control panel rather than a record kept in history.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// How long a line stays before anything may replace it.
    /// <para>This single constant is the whole anti-jitter rule (SP-0073 risk 3). Some sources rewrite
    /// the field on every segment of the stream, a few seconds apart, and without a floor the line would
    /// twitch continuously under a picture that is not changing at all.</para>
    /// <para>Ten seconds is anchored rather than picked: it is the control panel's own auto-hide
    /// timeout, so a viewer who wakes the panel sees at most one change of this line before the panel
    /// hides itself again. Against real content - a track runs minutes - the lag it can add is
    /// negligible.</para>
    /// <para>Deliberately a minimum hold and not a settle delay ("show it only once the same text has
    /// been read twice"). A settle delay would show nothing whatsoever on a source that never repeats a
    /// value, and that is the same source this feature exists to cover.</para>
    /// </summary>
    public static readonly TimeSpan MinimumHold = TimeSpan.FromSeconds(10);

    private TimeSpan _shownSince;

    /// <summary>What the line should say, or <c>null</c> when it should not be there at all.</summary>
    public string? Text { get; private set; }

    /// <summary>
    /// One reading from the open stream. Returns whether <see cref="Text"/> changed, so the caller can
    /// repaint and log only when something actually happened.
    /// </summary>
    /// <remarks>
    /// A blank reading is ignored rather than treated as "nothing is on air". Every re-open builds a new
    /// media whose metadata is empty until its first block arrives, so erasing on blank would blink the
    /// line off and back on through every reconnect and every quality switch - which is the flicker the
    /// ticket forbids, arriving by the back door. The events that genuinely end a broadcast are explicit
    /// and go through <see cref="Clear"/>.
    /// <para>A candidate refused by the hold is not remembered. The caller reads the stream again on its
    /// next tick, so a change that is still true will be offered again and taken then; remembering it
    /// would instead replay a value the stream has already moved on from.</para>
    /// </remarks>
    public bool Observe(TimeSpan now, string? reading)
    {
        var candidate = BroadcastText.Sanitize(reading, MaxLength);
        if (candidate is null || candidate == Text)
        {
            return false;
        }

        if (Text is not null && now - _shownSince < MinimumHold)
        {
            return false;
        }

        Text = candidate;
        _shownSince = now;
        return true;
    }

    /// <summary>
    /// The broadcast this line described is over - the channel changed, playback stopped, or the stream
    /// failed terminally. Erases immediately, with no residual hold: the ticket's rule is that a viewer
    /// never sees the previous channel's text under a new picture.
    /// </summary>
    /// <returns>Whether there was anything to erase.</returns>
    public bool Clear()
    {
        if (Text is null)
        {
            return false;
        }

        Text = null;
        _shownSince = TimeSpan.Zero;
        return true;
    }
}
