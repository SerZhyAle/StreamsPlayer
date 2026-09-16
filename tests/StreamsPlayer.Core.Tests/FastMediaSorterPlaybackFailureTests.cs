namespace StreamsPlayer.Core.Tests;

public sealed class FastMediaSorterPlaybackFailureTests
{
    [Fact]
    public void Classify_503FromTheLegItselfIsTheListenerLimit()
    {
        Assert.Equal(FastMediaSorterPlaybackFailureKind.ListenerLimit, FastMediaSorterPlaybackFailure.Classify(503));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(404)]
    public void Classify_EverythingElseStaysOnTheBoundedRecoveryPath(int? status)
    {
        Assert.Equal(FastMediaSorterPlaybackFailureKind.Recoverable, FastMediaSorterPlaybackFailure.Classify(status));
    }

    [Fact]
    public void GenericPolicyWouldRetry503_WhichIsWhyTheBroadcastRouteClassifiesItFirst()
    {
        var decision = new LivePlaybackRecoveryPolicy().Decide(new PlaybackFailureSignal("open_failed", HttpStatusCode: 503));

        Assert.Equal(RecoveryActionKind.Reconnect, decision.Kind);
    }

    [Fact]
    public void CleanEndSpendsABoundedBudgetAndThenHardFails()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        RecoveryDecision decision;
        var attempts = 0;
        do
        {
            decision = policy.Decide(new PlaybackFailureSignal("end_reached", EndReached: true));
            attempts++;
        }
        while (decision.Kind == RecoveryActionKind.Reconnect && attempts < 10);

        Assert.Equal(RecoveryActionKind.HardFail, decision.Kind);
        Assert.True(attempts <= 3, $"expected a small bounded budget, took {attempts} decisions");
    }

    [Theory]
    [InlineData("https://radio.example/stream.aac")]
    [InlineData("http://example.com/listen")]
    [InlineData("http://192.168.1.20:8768/other.aac")]
    public void ArbitraryHttpAddressIsNotABroadcastEvenWhenMarkedLive(string url)
    {
        var channel = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = "Manual",
            MediaKind = MediaKind.Audio,
            SourceOrigin = SourceOrigin.Manual,
            AddedAt = DateTimeOffset.UnixEpoch,
            IsLive = true
        };

        Assert.False(FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(channel));
        Assert.True(FastMediaSorterBroadcastImport.IsLive(channel));
    }

    [Theory]
    [InlineData("http://192.168.1.20:8768/live-audio.aac")]
    [InlineData("http://192.168.1.20:8768/live-audio")]
    [InlineData("http://10.0.0.7:41234/listen")]
    public void PhoneAndWatchAddressShapesAreBroadcasts(string url)
    {
        var channel = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = "Device",
            MediaKind = MediaKind.Audio,
            SourceOrigin = SourceOrigin.Manual,
            AddedAt = DateTimeOffset.UnixEpoch
        };

        Assert.True(FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(channel));
        Assert.True(FastMediaSorterBroadcastImport.IsLive(channel));
    }
}
