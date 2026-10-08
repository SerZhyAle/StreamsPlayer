using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0208: the buffer a video or RTSP broadcast opens with comes from the descriptor's
/// <c>targetLatencyMs</c>, inside the bounds that keep <c>LIVE-BROADCAST</c> consumer rules 1 and 2.
/// A catalog stream keeps the product's own setting.
/// </summary>
public sealed class BroadcastLiveCacheTests
{
    private static StreamChannel Channel(MediaKind kind, FastMediaSorterBroadcastInfo? broadcast) => new()
    {
        Id = Guid.NewGuid(),
        Url = "rtsp://192.168.1.20:8554/live",
        Title = "Phone camera",
        MediaKind = kind,
        SourceOrigin = SourceOrigin.Imported,
        AddedAt = DateTimeOffset.UnixEpoch,
        FastMediaSorterBroadcast = broadcast,
    };

    private static FastMediaSorterBroadcastInfo Info(long? targetLatencyMs) => new()
    {
        Mode = "VIDEO_ONLY",
        SelectedTransport = "RTSP",
        TargetLatencyMs = targetLatencyMs,
    };

    [Theory]
    [InlineData(200L, 500u)]      // RTSP kinds state 200: the floor wins against Wi-Fi jitter
    [InlineData(1_000L, 1_000u)]  // HTTP MPEG-TS video (item H)
    [InlineData(2_000L, 1_500u)]  // a relay figure: the ceiling keeps rule 2's 2 s
    [InlineData(60_000L, 1_500u)]
    public void ABroadcastTakesItsStatedLatencyInsideTheBounds(long stated, uint expected)
    {
        Assert.Equal(expected, BroadcastLiveCache.For(Channel(MediaKind.Rtsp, Info(stated))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void AnUnstatedOrNonsenseLatencyFallsBackToTheHttpFigure(long? stated)
    {
        Assert.Equal(BroadcastLiveCache.UnstatedMilliseconds, BroadcastLiveCache.For(Channel(MediaKind.Video, Info(stated))));
    }

    [Fact]
    public void ACatalogStreamAndAnAudioBroadcastAreLeftToTheProductSetting()
    {
        Assert.Null(BroadcastLiveCache.For(Channel(MediaKind.Video, null)));
        Assert.Null(BroadcastLiveCache.For(Channel(MediaKind.Audio, Info(200))));
    }

    [Fact]
    public void TheBoundsKeepTheSecondRuleOfTheContract()
    {
        Assert.True(BroadcastLiveCache.FloorMilliseconds < BroadcastLiveCache.CeilingMilliseconds);
        Assert.True(BroadcastLiveCache.CeilingMilliseconds < BroadcastLiveCache.ExchangeCeilingMilliseconds);
    }

    /// <summary>
    /// SP-0203 rule 4: the buffer follows the endpoint the leg opens, and an exchange endpoint may
    /// rise to the 2000 ms the contract names for the relay - and no further.
    /// </summary>
    [Theory]
    [InlineData("RELAY", 2_000L, 2_000u)] // the contract's relay figure
    [InlineData("TUNNEL", 2_000L, 2_000u)]
    [InlineData("RELAY", 60_000L, 2_000u)] // a rogue latency does not become a rogue buffer
    [InlineData("RELAY", null, 500u)]     // endpoint unstated: the stored channel figure applies
    [InlineData("HTTP", 2_000L, 1_500u)]  // a LAN endpoint keeps the LAN ceiling
    [InlineData("RTSP", 2_000L, 1_500u)]
    public void TheExchangeEndpointMayRiseToTwoSeconds(string transport, long? stated, uint expected)
    {
        var channel = Channel(MediaKind.Rtsp, Info(500));
        var endpoint = new FastMediaSorterBroadcastEndpoint(
            transport == "RELAY" ? "https://relay.example.net/v2/b/b1/stream" : "rtsp://192.168.1.20:8554/live",
            transport, "VIDEO_ONLY", null, null, null, null, true, stated, null, null);
        Assert.Equal(expected, BroadcastLiveCache.For(channel, endpoint));
    }

    [Fact]
    public void AnEndpointStatingNothingOnAChannelWithNothingFallsBackToTheHttpFigure()
    {
        var channel = Channel(MediaKind.Rtsp, Info(null));
        var endpoint = new FastMediaSorterBroadcastEndpoint(
            "https://relay.example.net/v2/b/b1/stream", "RELAY", "VIDEO_ONLY",
            null, null, null, null, true, null, null, null);
        Assert.Equal(BroadcastLiveCache.UnstatedMilliseconds, BroadcastLiveCache.For(channel, endpoint));
    }

    [Fact]
    public void TheNullEndpointPathIsTheChannelLevelReading()
    {
        var channel = Channel(MediaKind.Rtsp, Info(2_000));
        Assert.Equal(BroadcastLiveCache.For(channel), BroadcastLiveCache.For(channel, null));
    }
}
