namespace StreamsPlayer.Core;

/// <summary>
/// The platform-neutral live-recovery state machine (<c>docs/specifications/streams.txt</c>, Part D).
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

    // Part D leaves no explicit backoff for a stall or a stream-end re-open; a short fixed delay avoids
    // a tight reconnect loop without adding perceptible latency to a recovery.
    private static readonly TimeSpan StallBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StreamEndedBackoff = TimeSpan.FromSeconds(1);

    private int _behindLiveWindowAttempts;
    private int _transientAttempts;
    private int _stallAttempts;
    private int _streamEndedAttempts;

    /// <summary>Classifies the signal and returns the next recovery action for its trigger.</summary>
    public RecoveryDecision Decide(PlaybackFailureSignal signal)
    {
        var trigger = PlaybackRecoveryClassifier.Classify(signal);
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

    /// <summary>Clears all consecutive-attempt counters.</summary>
    public void Reset()
    {
        _behindLiveWindowAttempts = 0;
        _transientAttempts = 0;
        _stallAttempts = 0;
        _streamEndedAttempts = 0;
    }

    private (int Attempt, int Budget, TimeSpan Delay) Advance(RecoveryTrigger trigger) => trigger switch
    {
        // Linear 1 s / 2 s / 3 s.
        RecoveryTrigger.BehindLiveWindow => (
            ++_behindLiveWindowAttempts, BehindLiveWindowBudget, TimeSpan.FromSeconds(_behindLiveWindowAttempts)),
        // Exponential 2 / 4 / 8 / 16 s.
        RecoveryTrigger.Transient => (
            ++_transientAttempts, TransientBudget, TimeSpan.FromSeconds(Math.Pow(2, _transientAttempts))),
        RecoveryTrigger.Stall => (++_stallAttempts, StallBudget, StallBackoff),
        RecoveryTrigger.StreamEnded => (++_streamEndedAttempts, StreamEndedBudget, StreamEndedBackoff),
        _ => (0, 0, TimeSpan.Zero)
    };
}
