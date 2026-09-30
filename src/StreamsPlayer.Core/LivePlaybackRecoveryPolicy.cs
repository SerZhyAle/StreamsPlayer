namespace StreamsPlayer.Core;

/// <summary>
/// The platform-neutral live-recovery state machine (<c>DEVELOPER_PROMPT.md</c> Part D).
/// Given a <see cref="PlaybackFailureSignal"/> it returns whether to reconnect (after a bounded backoff)
/// or hard-fail, tracking a separate <em>consecutive</em>-attempt budget per <see cref="RecoveryTrigger"/>.
/// Reaching sustained live playback (<see cref="NotifyLive"/>) resets every budget, so a stream that keeps
/// recovering (e.g. a looping playlist) is never starved, while a genuinely dead stream terminates quickly.
/// Holds no timer or ambient clock: the caller applies <see cref="RecoveryDecision.Delay"/>.
/// </summary>
public sealed class LivePlaybackRecoveryPolicy
{
    // Part D retry budgets, with one deliberate divergence from the Android reference.
    private const int BehindLiveWindowBudget = 3;
    // SP-0079, owner decision of 2026-08-08: the two budgets Part D set to four are two here. Four
    // transient attempts on the shipped exponential backoff means roughly two minutes of black screen
    // before the user is told anything actionable, and SP-0072 put that wait on screen where it can now
    // be read - which is what made its length the visible problem. Two attempts hard-fail at about
    // thirty seconds, and the failure dialog offers Retry, so nothing that would have recovered on
    // attempt three is lost; it just stops costing ninety silent seconds to find out.
    private const int TransientBudget = 2;
    private const int StallBudget = 3;
    private const int StreamEndedBudget = 2;
    // SP-0096, owner decision of 2026-09-09: one re-open and then the verdict. The trigger fires only
    // after PlaybackOpenBudget.OpenDeadline has already elapsed once, so every extra attempt costs
    // another full deadline of black screen - two attempts would be worse than the sixty-five-second
    // wait this rule exists to end.
    private const int OpenTimeoutBudget = 1;

    // SP-0041 Decision 1: every backoff below is half of what Part D (and this class before it) used.
    // Each leg already spends the engine's own open timeout, so a longer pause bought little but more
    // black screen - and the connectivity gate in front of this policy now settles the dead-host and
    // no-network cases a longer wait was hoping to outlast. StreamsPlayer's values, a recorded
    // divergence from the reference (docs/PLAYBACK_RESILIENCE.md section 4).
    //
    // Part D leaves no explicit backoff for a stall or a stream-end re-open; a short fixed delay avoids
    // a tight reconnect loop without adding perceptible latency to a recovery.
    private static readonly TimeSpan StallBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StreamEndedBackoff = TimeSpan.FromMilliseconds(500);
    // SP-0096: not the transient 1 s/2 s ladder. Twenty seconds have already been spent waiting by the
    // time this trigger fires; the delay exists only so the re-open is not a tight loop.
    private static readonly TimeSpan OpenTimeoutBackoff = TimeSpan.FromMilliseconds(500);

    private int _behindLiveWindowAttempts;
    private int _transientAttempts;
    private int _stallAttempts;
    private int _streamEndedAttempts;
    private int _openTimeoutAttempts;

    /// <summary>Classifies the signal and returns the next recovery action for its trigger.</summary>
    public RecoveryDecision Decide(PlaybackFailureSignal signal)
        => Decide(PlaybackRecoveryClassifier.Classify(signal));

    /// <summary>
    /// The decision for an already-classified trigger. A trigger with no arm in the budget table - one
    /// appended to the enum without a budget - hard-fails on its first occurrence instead of reconnecting
    /// forever with no delay (SP-0178).
    /// </summary>
    internal RecoveryDecision Decide(RecoveryTrigger trigger)
    {
        if (trigger == RecoveryTrigger.HardFail)
        {
            return new RecoveryDecision(RecoveryActionKind.HardFail, TimeSpan.Zero, 0, 0, RecoveryTrigger.HardFail);
        }

        var (attempt, budget, delay) = Advance(trigger);
        return attempt > budget
            ? new RecoveryDecision(RecoveryActionKind.HardFail, TimeSpan.Zero, attempt, budget, trigger)
            : new RecoveryDecision(RecoveryActionKind.Reconnect, delay, attempt, budget, trigger);
    }

    /// <summary>Resets every budget after the stream reaches sustained live playback.</summary>
    public void NotifyLive() => Reset();

    /// <summary>
    /// SP-0169: how long a leg must have played before its recovery budget is handed back. Reaching Playing
    /// proves only that a connection opened; a station that opens and drops within seconds - over and over -
    /// would otherwise start every leg with a full budget and never reach the terminal dialog.
    /// </summary>
    public static readonly TimeSpan SustainedLiveAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// SP-0169: a leg that was playing has ended after <paramref name="playedFor"/>. Restores every budget
    /// only when that was long enough to count as sustained; a shorter leg leaves the spent attempts spent.
    /// </summary>
    public void NotifyLegPlayed(TimeSpan playedFor)
    {
        if (playedFor >= SustainedLiveAfter)
        {
            Reset();
        }
    }

    /// <summary>Clears all consecutive-attempt counters.</summary>
    public void Reset()
    {
        _behindLiveWindowAttempts = 0;
        _transientAttempts = 0;
        _stallAttempts = 0;
        _streamEndedAttempts = 0;
        _openTimeoutAttempts = 0;
    }

    private (int Attempt, int Budget, TimeSpan Delay) Advance(RecoveryTrigger trigger) => trigger switch
    {
        // Linear 0.5 / 1 / 1.5 s (SP-0041: half of Part D's 1 / 2 / 3 s).
        RecoveryTrigger.BehindLiveWindow => (
            ++_behindLiveWindowAttempts, BehindLiveWindowBudget, TimeSpan.FromSeconds(_behindLiveWindowAttempts * 0.5)),
        // Exponential 1 / 2 s within SP-0079's budget of two (SP-0041: half of Part D's 2 / 4 / 8 / 16 s).
        RecoveryTrigger.Transient => (
            ++_transientAttempts, TransientBudget, TimeSpan.FromSeconds(Math.Pow(2, _transientAttempts - 1))),
        RecoveryTrigger.Stall => (++_stallAttempts, StallBudget, StallBackoff),
        RecoveryTrigger.StreamEnded => (++_streamEndedAttempts, StreamEndedBudget, StreamEndedBackoff),
        RecoveryTrigger.OpenTimeout => (++_openTimeoutAttempts, OpenTimeoutBudget, OpenTimeoutBackoff),
        // Attempt 1 against budget 0: an unmapped trigger is a hard failure, never a free reconnect.
        _ => (1, 0, TimeSpan.Zero)
    };
}
