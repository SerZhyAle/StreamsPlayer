namespace StreamsPlayer.Core.Tests;

/// <summary>
/// S6-1: with no endpoint left to advance to, which refusals of a video broadcast leg go to the bounded recovery and which are
/// the verdict. A live leg whose transport failed and a relay's 503 are blips; a leg that never showed a picture, and every
/// refusal no reconnect can mend, are not.
/// </summary>
public sealed class BroadcastRecoveryHandOffTests
{
    [Theory]
    [InlineData("IOException")]
    [InlineData("SocketException")]
    [InlineData("relay-refused-503")]
    [InlineData("relay-refused-502")]
    [InlineData("relay-head-unparsable")]
    [InlineData("cancelled")]
    public void ALegThatWasLiveHandsEveryReconnectableFailureToTheRecovery(string reason)
    {
        Assert.True(BroadcastAttemptAdvance.HandsToRecovery(reachedLive: true, reason));
    }

    [Theory]
    [InlineData("IOException")]
    [InlineData("SocketException")]
    [InlineData("relay-refused-502")]
    [InlineData("relay-refused-404")]
    [InlineData("relay-refused-410")]
    [InlineData("relay-head-unparsable")]
    [InlineData("unsupported_address")]
    [InlineData("play_rejected")]
    [InlineData("lan-refused")]
    [InlineData("lan-404")]
    [InlineData("lan-410")]
    [InlineData("broadcast-http-refused-404")]
    [InlineData("ended")]
    [InlineData(null)]
    public void APlainRefusalBeforeThePictureWasEverShownIsTheVerdict(string? reason)
    {
        Assert.False(BroadcastAttemptAdvance.HandsToRecovery(reachedLive: false, reason));
    }

    [Fact]
    public void ARelayStallIsRecoveredWhetherOrNotTheLegWasLive()
    {
        Assert.True(BroadcastAttemptAdvance.HandsToRecovery(reachedLive: false, "relay-refused-503"));
        Assert.True(BroadcastAttemptAdvance.HandsToRecovery(reachedLive: true, "relay-refused-503"));
    }

    [Theory]
    [InlineData(ExchangeBroadcastAuth.PasswordRequiredReason)]
    [InlineData(ExchangeBroadcastAuth.PasswordRefusedReason)]
    [InlineData(ExchangeBroadcastAuth.UnsupportedEndpointReason)]
    [InlineData(ExchangeBroadcastCertificateException.Refused)]
    [InlineData(ExchangeBroadcastCertificateException.UntrustedWithoutFingerprint)]
    [InlineData("key-changed")]
    [InlineData("e2e-missing")]
    [InlineData(ExchangeBroadcastResources.CapacityReason)]
    public void ARefusalNoReconnectCanMendStaysTheVerdictLiveOrNot(string reason)
    {
        Assert.False(BroadcastAttemptAdvance.HandsToRecovery(reachedLive: true, reason));
        Assert.False(BroadcastAttemptAdvance.HandsToRecovery(reachedLive: false, reason));
    }

    [Fact]
    public void TheRelay503IsAStallNotTheDevicesListenerLimit()
    {
        // The listener limit is a status of the device's own first answer, read by the audio leg; a video leg's reason carries
        // none, so the relay's 503 reason is read as the stall item DX describes and never as the limit.
        Assert.Null(IncomingBroadcastFailure.TerminalMessageKey("relay-refused-503", null));
        Assert.True(BroadcastAttemptAdvance.HandsToRecovery(reachedLive: false, "relay-refused-503"));
    }
}
