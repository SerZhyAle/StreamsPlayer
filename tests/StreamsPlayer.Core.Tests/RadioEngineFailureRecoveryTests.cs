using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0189: a radio engine error is judged by what the engine reported and what the player established, not by
/// the type of an exception the player created to carry it. The SP-0103 rows in
/// <see cref="LivePlaybackRecoveryPolicyTests"/> stay as written; these pin the cases this ticket changed.
/// </summary>
public sealed class RadioEngineFailureRecoveryTests
{
    // The tokens the radio now reports (shared with the video path).
    private const string EngineError = "encountered_error";
    private const string PlayRejected = "play_rejected";

    private static PlaybackFailureSignal EngineErrorSignal() => new(EngineError);

    [Fact]
    public void Classify_AnEngineErrorWithNoEstablishedCauseIsTransient()
    {
        // Criterion 3. Every radio engine error observed in the research was a network or server cause, and an
        // unknown cause is never final by default: the budget bounds it.
        Assert.Equal(RecoveryTrigger.Transient, PlaybackRecoveryClassifier.Classify(EngineErrorSignal()));
    }

    [Theory]
    [InlineData(PlayRejected)]
    [InlineData("VLCException")]
    [InlineData("connection refused")]
    public void Classify_AnEstablishedLocalEngineFailureIsFinalWhateverItsText(string reason)
    {
        // Criterion 2. The flag is the player's own verdict; a network word in the reason must not soften it.
        Assert.Equal(RecoveryTrigger.HardFail, PlaybackRecoveryClassifier.Classify(
            new PlaybackFailureSignal(reason, LocalEngineFailure: true)));
    }

    [Fact]
    public void Classify_LocalEngineFailureOutranksARetryableStatus()
    {
        Assert.Equal(RecoveryTrigger.HardFail, PlaybackRecoveryClassifier.Classify(
            new PlaybackFailureSignal(PlayRejected, HttpStatusCode: 503, LocalEngineFailure: true)));
    }

    [Fact]
    public void Classify_AnEngineErrorStillHardFailsOnTheStatusTheProbeFound()
    {
        // The contract's split (Part D) is untouched: a 4xx other than 429 found by the status probe is final.
        Assert.Equal(RecoveryTrigger.HardFail, PlaybackRecoveryClassifier.Classify(
            new PlaybackFailureSignal(EngineError, HttpStatusCode: 404)));
    }

    [Fact]
    public void EngineErrors_SpendTheBudgetAttemptByAttemptBeforeTheVerdict()
    {
        // Criterion 1: the terminal failure comes only after the budget is spent.
        var policy = new LivePlaybackRecoveryPolicy();

        var first = policy.Decide(EngineErrorSignal());
        var second = policy.Decide(EngineErrorSignal());
        var third = policy.Decide(EngineErrorSignal());

        Assert.Equal((RecoveryActionKind.Reconnect, RecoveryTrigger.Transient, 1, 2), (first.Kind, first.Trigger, first.Attempt, first.Budget));
        Assert.Equal((RecoveryActionKind.Reconnect, RecoveryTrigger.Transient, 2, 2), (second.Kind, second.Trigger, second.Attempt, second.Budget));
        Assert.Equal((RecoveryActionKind.HardFail, RecoveryTrigger.Transient, 3, 2), (third.Kind, third.Trigger, third.Attempt, third.Budget));
    }

    [Fact]
    public void TheOwnersSequence_AFailedReopenAfterAStreamEndIsNotTerminal()
    {
        // Criterion 5, the evidence of 2026-10-01: about 17 hours on one station, the stream ended, one reconnect,
        // and that re-open failed with an engine error. It used to end the session at attempt 0 of budget 0.
        var policy = new LivePlaybackRecoveryPolicy();
        policy.NotifyLegPlayed(TimeSpan.FromHours(17));

        var ended = policy.Decide(new PlaybackFailureSignal("end_reached", EndReached: true));
        var reopenFailed = policy.Decide(EngineErrorSignal());

        Assert.Equal((RecoveryActionKind.Reconnect, RecoveryTrigger.StreamEnded, 1), (ended.Kind, ended.Trigger, ended.Attempt));
        Assert.Equal((RecoveryActionKind.Reconnect, RecoveryTrigger.Transient, 1, 2), (reopenFailed.Kind, reopenFailed.Trigger, reopenFailed.Attempt, reopenFailed.Budget));
    }

    [Fact]
    public void ALocalEngineFailure_FailsAtOnceWithoutSpendingAnAttempt()
    {
        var policy = new LivePlaybackRecoveryPolicy();

        var local = policy.Decide(new PlaybackFailureSignal(PlayRejected, LocalEngineFailure: true));
        var later = policy.Decide(EngineErrorSignal());

        Assert.Equal((RecoveryActionKind.HardFail, RecoveryTrigger.HardFail, 0, 0), (local.Kind, local.Trigger, local.Attempt, local.Budget));
        Assert.Equal(1, later.Attempt);
    }

    [Fact]
    public void TheReportCategoryOfAnEngineErrorIsUnchanged()
    {
        // The radio used to report InvalidOperationException, which the shareable report filed as a media error;
        // the token it reports now files the same way, so the user's report reads as before.
        Assert.Equal(PlaybackErrorClassifier.Classify("InvalidOperationException"), PlaybackErrorClassifier.Classify(EngineError));
    }
}
