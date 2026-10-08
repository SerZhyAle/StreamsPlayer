namespace StreamsPlayer.Core;

/// <summary>SP-0096: what <see cref="PlaybackOpenBudget.Observe"/> concluded about the leg being opened.</summary>
public enum PlaybackOpenVerdict
{
    /// <summary>Still inside the budget. Keep waiting.</summary>
    None,

    /// <summary>Nothing has arrived from the source at all: it is not answering, and re-opening it would
    /// only repeat the silence.</summary>
    DeadSource,

    /// <summary>The source answered but never reached the screen inside the budget.</summary>
    Deadline
}

/// <summary>
/// SP-0096: how long the player waits for a stream that has been opened but has not yet played. The
/// companion to <see cref="PlaybackFreezeDetector"/> and its exact inverse in scope - that rule watches
/// a stream that has been live at least once and is deliberately silent before then (SP-0070 acceptance
/// 4), which left the whole pre-live window unsupervised. A source that raises no error and no
/// end-of-stream simply waited forever: measured on the owner's machine on 2026-09-06/08, twelve
/// sessions ended having never played, at 9.8 s, 16 s, 17 s, 24 s, 38 s and 65 s of black screen, each
/// one carrying <c>legs=1 | reconnects=0</c> - the application had done nothing at all.
///
/// <para>The rule has two branches because "not answering" and "answering slowly" deserve different
/// answers. A host that has delivered no bytes at all by <see cref="DeadSourceAfter"/> is dead: there is
/// nothing to re-open, so the caller fails it outright. A host that is delivering but has not reached
/// the screen by <see cref="OpenDeadline"/> gets the one re-open its
/// <see cref="RecoveryTrigger.OpenTimeout"/> budget allows, and then the same verdict.</para>
///
/// <para>Same division of labour as every other rule here: the App owns the clock, the timer and the
/// engine; this type only answers the question, so the behaviour is decidable - and testable - without
/// a window, a network, or a media backend. Time is a parameter on every call, never read ambiently,
/// and it must be a reading of the <em>current leg</em>: a session-wide clock would make the second
/// re-open expire the instant it started.</para>
///
/// <para>The type is not thread-safe: the player feeds it from the UI thread only.</para>
/// </summary>
public sealed class PlaybackOpenBudget
{
    /// <summary>
    /// How long a source may deliver nothing whatsoever before it counts as not answering.
    /// <para>Eight seconds, with a fourfold margin over what a healthy open needs. A working HLS open
    /// moves the byte counter within its first observation - a logged healthy open read
    /// <c>read_bytes=3046</c> two seconds in - so any source still at zero here is not slow, it is
    /// absent. The margin is what keeps a congested-but-alive source out of this branch; such a source
    /// falls through to <see cref="OpenDeadline"/>, which is the lenient one.</para>
    /// </summary>
    public static readonly TimeSpan DeadSourceAfter = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The whole budget for one open, whatever the source is doing.
    /// <para>Twenty seconds, chosen against measurement rather than taste: over 311 timed openings in
    /// the owner's archives the median first frame arrived at 2.4 s, the 90th percentile at 5.3 s and
    /// the 99th at 11.2 s. Twenty clears the 99th percentile with room and costs exactly two channels
    /// out of 311 (24 s and 31 s). Raising it buys those two back at the price of doubling the wait on
    /// every dead one; lowering it starts eating the p99 tail, where the channels are real.</para>
    /// </summary>
    public static readonly TimeSpan OpenDeadline = TimeSpan.FromSeconds(20);

    // SP-0203: the dead-source branch doubles as the attempt's connect bound - the engine cannot
    // report the TCP connect boundary, so "no bytes from this attempt" is the honest proxy for it. A
    // LAN attempt gets a short slice (the attempt list moves on); an exchange attempt keeps the
    // measured default, because the relay is expected to be the endpoint that works.
    private readonly TimeSpan _attemptDeadSourceAfter;

    public PlaybackOpenBudget(TimeSpan? attemptDeadSourceAfter = null)
    {
        _attemptDeadSourceAfter = attemptDeadSourceAfter ?? DeadSourceAfter;
    }

    private bool _sourceAnswered;
    private bool _reported;
    private bool _live;

    /// <summary>
    /// Forgets everything about the current leg. The caller invokes this per media open: a new media
    /// restarts the engine's counters from zero, and a re-open is entitled to its own budget.
    /// </summary>
    public void Reset()
    {
        _sourceAnswered = false;
        _reported = false;
        _live = false;
    }

    /// <summary>
    /// Stops the budget for good: the stream reached the screen, and from here it is the freeze rule's
    /// stream, not this one's. Called on first live rather than tested by the caller, so a stream that
    /// went live half a second before its deadline cannot be killed by the observation that follows.
    /// </summary>
    public void NotifyLive() => _live = true;

    /// <summary>
    /// One observation, at whatever cadence the caller already polls the engine.
    /// </summary>
    /// <param name="sinceOpen">Monotonic time since this leg was opened.</param>
    /// <param name="receivedBytes">
    /// Total bytes taken off the network on this leg, or null where the engine reports none. Null is
    /// "no evidence", never "nothing arrived": an engine without the counter must lose the dead-source
    /// branch and keep the deadline, not be condemned by its own silence.
    /// </param>
    /// <returns>
    /// A verdict exactly once per leg, after which the budget is disarmed until <see cref="Reset"/>.
    /// Reporting once is what stops the next tick raising a second dialog over the first.
    /// </returns>
    public PlaybackOpenVerdict Observe(TimeSpan sinceOpen, long? receivedBytes) =>
        Observe(sinceOpen, sinceOpen, receivedBytes);

    /// <summary>
    /// The attempt-aware reading: <paramref name="sinceAttempt"/> is the current endpoint's own
    /// clock, <paramref name="sinceOpen"/> the leg's. The dead-source branch judges the attempt; the
    /// deadline judges the whole leg, so a list of slowly-answering endpoints cannot stretch one leg
    /// without end.
    /// </summary>
    public PlaybackOpenVerdict Observe(TimeSpan sinceOpen, TimeSpan sinceAttempt, long? receivedBytes)
    {
        if (_live || _reported)
        {
            return PlaybackOpenVerdict.None;
        }

        if (receivedBytes is not { } bytes || bytes > 0)
        {
            // Either the source has answered, or this engine cannot say. Both retire the dead-source
            // branch for this leg - and permanently, because a counter that moved once has proven the
            // source exists even if it has since gone quiet. Going quiet after answering is the
            // deadline's business.
            _sourceAnswered = true;
        }

        if (sinceOpen >= OpenDeadline)
        {
            _reported = true;
            return PlaybackOpenVerdict.Deadline;
        }

        if (!_sourceAnswered && sinceAttempt >= _attemptDeadSourceAfter)
        {
            _reported = true;
            return PlaybackOpenVerdict.DeadSource;
        }

        return PlaybackOpenVerdict.None;
    }
}
