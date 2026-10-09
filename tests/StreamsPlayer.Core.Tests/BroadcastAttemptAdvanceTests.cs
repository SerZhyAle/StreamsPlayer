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
}
