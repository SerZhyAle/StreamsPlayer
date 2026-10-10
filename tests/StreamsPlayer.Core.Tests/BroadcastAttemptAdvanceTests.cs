namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0203 / SP-0180 wave C: the advance to the producer's next endpoint is a pre-live move. A playing
/// leg that errors must reconnect from the top of the list through recovery, not slide from LAN to relay.
/// </summary>
public sealed class BroadcastAttemptAdvanceTests
{
    private static bool CanAdvance(
        bool closing = false,
        bool failureShown = false,
        bool probeLegPending = false,
        bool reachedLive = false,
        bool recoveryInFlight = false,
        bool legOpenInFlight = false,
        int attemptIndex = 0,
        int attemptCount = 2) =>
        BroadcastAttemptAdvance.CanAdvance(
            closing, failureShown, probeLegPending, reachedLive, recoveryInFlight, legOpenInFlight,
            attemptIndex, attemptCount);

    [Fact]
    public void APreLiveFailureWithAnotherEndpointAdvances() =>
        Assert.True(CanAdvance());

    [Fact]
    public void AnErrorOnALegThatWasLiveNeverAdvances() =>
        Assert.False(CanAdvance(reachedLive: true));

    [Fact]
    public void ARecoveryThatIsStillDecidingOwnsTheNextOpen() =>
        Assert.False(CanAdvance(recoveryInFlight: true, legOpenInFlight: false));

    [Fact]
    public void TheFirstAttemptOfARecoveryLegMayStillAdvanceWhileItsOpenIsOutstanding() =>
        Assert.True(CanAdvance(recoveryInFlight: true, legOpenInFlight: true));

    [Fact]
    public void ALegThatWasLiveNeverAdvancesEvenInsideItsRecoveryOpen() =>
        Assert.False(CanAdvance(reachedLive: true, recoveryInFlight: true, legOpenInFlight: true));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(0, 1)]
    public void TheLastEndpointOfTheListHasNowhereToAdvance(int attemptIndex, int attemptCount) =>
        Assert.False(CanAdvance(attemptIndex: attemptIndex, attemptCount: attemptCount));

    [Fact]
    public void AClosingWindowAFinalVerdictAndAProbeLegNeverAdvance()
    {
        Assert.False(CanAdvance(closing: true));
        Assert.False(CanAdvance(failureShown: true));
        Assert.False(CanAdvance(probeLegPending: true));
    }

    // SP-0180 re-audit F1: a report raised under an attempt that has since been retired is not the
    // current attempt's, whichever way round the two epochs differ.
    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    [InlineData(4, 3, false)]
    public void AReportIsCurrentOnlyWhileItsAttemptIsTheOpenOne(int reportEpoch, int currentEpoch, bool expected) =>
        Assert.Equal(expected, BroadcastAttemptAdvance.IsCurrentAttempt(reportEpoch, currentEpoch));

    // F1 / F2: the duplicate error that already advanced attempt 0 reaches the UI after the advance and is
    // refused, so attempt 1 is not failed by attempt 0's second report and the list still has a turn for 2.
    [Fact]
    public void ADuplicateErrorOfTheAdvancedAttemptCannotSkipTheNextEndpoint()
    {
        var epoch = 1;
        var duplicateRaisedUnder = epoch; // both raised by attempt 0
        var advances = 0;

        void Report(int raisedUnder)
        {
            if (BroadcastAttemptAdvance.IsCurrentAttempt(raisedUnder, epoch) &&
                CanAdvance(attemptIndex: advances, attemptCount: 3))
            {
                advances++;
                epoch++; // RetireAttempt
            }
        }

        Report(duplicateRaisedUnder); // the first error advances 0 -> 1
        Report(duplicateRaisedUnder); // the duplicate arrives after the advance
        Assert.Equal(1, advances);

        Report(epoch); // attempt 1's own genuine error is still current and advances 1 -> 2
        Assert.Equal(2, advances);
    }

    // F2: the pre-live end of stream is judged by the same rule as an engine error; a live leg's end is not.
    [Fact]
    public void AnEndBeforeTheFirstPictureAdvancesButALiveLegsEndGoesToRecovery()
    {
        Assert.True(CanAdvance(reachedLive: false, attemptIndex: 0, attemptCount: 2));
        Assert.False(CanAdvance(reachedLive: true, attemptIndex: 0, attemptCount: 2));
    }

    // F4: the mark belongs to the leg whose recovery opened it.
    [Fact]
    public void AnEarlierOpensExitDoesNotClearALaterOpensMark()
    {
        var tracker = new RecoveryLegOpenTracker();
        tracker.Begin(leg: 2);
        tracker.Begin(leg: 3);

        tracker.End(leg: 2);

        Assert.True(tracker.IsOutstanding(3));
        Assert.False(tracker.IsOutstanding(2));
    }

    [Fact]
    public void ALegWithNoRecoveryOpenIsNeverMarked()
    {
        var tracker = new RecoveryLegOpenTracker();
        Assert.False(tracker.IsOutstanding(1));

        // A stuck open of another leg was never registered by a recovery, so the deciding recovery's
        // current leg is unmarked and the gate keeps the next open for the recovery.
        tracker.Begin(leg: 1);
        Assert.False(tracker.IsOutstanding(2));
        Assert.False(CanAdvance(recoveryInFlight: true, legOpenInFlight: tracker.IsOutstanding(2)));
    }

    [Fact]
    public void TheMarkIsClearedByEveryExitOfItsOwnOpenAndNeverGoesNegative()
    {
        var tracker = new RecoveryLegOpenTracker();
        tracker.Begin(5);
        tracker.Begin(5);
        tracker.End(5);
        Assert.True(tracker.IsOutstanding(5));
        tracker.End(5);
        Assert.False(tracker.IsOutstanding(5));

        tracker.End(5); // an unmatched exit
        tracker.Begin(5);
        Assert.True(tracker.IsOutstanding(5));
    }

    [Fact]
    public void TheMarkSurvivesTheLegsAdvanceToItsNextEndpoint()
    {
        // The leg number does not move when the attempt does, so a recovery open that is still outstanding
        // keeps letting the second and the third endpoint of that leg advance.
        var tracker = new RecoveryLegOpenTracker();
        tracker.Begin(7);
        Assert.True(CanAdvance(recoveryInFlight: true, legOpenInFlight: tracker.IsOutstanding(7), attemptIndex: 1, attemptCount: 3));
    }
}
