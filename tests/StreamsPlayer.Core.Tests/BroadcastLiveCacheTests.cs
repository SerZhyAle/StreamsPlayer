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
    [InlineData("RELAY", null, 2_000u)]   // item V: a relay never takes the LAN stream's top-level figure
    [InlineData("TUNNEL", null, 2_000u)]
    [InlineData("HTTP", null, 500u)]      // item V: a LAN endpoint stating none takes the top-level figure
    [InlineData("RTSP", null, 500u)]
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

    /// <summary>
    /// SP-0281, <c>LIVE-BROADCAST</c> item V: an endpoint and a descriptor that state nothing take the default of the
    /// endpoint's transport - <c>RTSP</c> 200 (item L's floor then lifts it to 500), <c>HTTP</c> 1000, and 2000 for
    /// <c>RELAY</c> and <c>TUNNEL</c> - and item L's floor and ceiling bound the default as they bound a stated figure.
    /// </summary>
    [Theory]
    [InlineData("RTSP", 500u)]
    [InlineData("HTTP", 1_000u)]
    [InlineData("RELAY", 2_000u)]
    [InlineData("TUNNEL", 2_000u)]
    [InlineData("P2P", 1_000u)]
    public void AnEndpointStatingNothingOnAChannelWithNothingTakesTheDefaultOfItsTransport(string transport, uint expected)
    {
        var channel = Channel(MediaKind.Rtsp, Info(null));
        var endpoint = new FastMediaSorterBroadcastEndpoint(
            "https://relay.example.net/v2/b/b1/stream", transport, "VIDEO_ONLY",
            null, null, null, null, true, null, null, null);
        Assert.Equal(expected, BroadcastLiveCache.For(channel, endpoint));
    }

    /// <summary>The cases of the contract's "Cases a reader is held to" for item V that read a top-level figure of 200.</summary>
    [Theory]
    [InlineData("RTSP", 500u)]     // the top-level 200 of an RTSP endpoint, lifted to the floor
    [InlineData("HTTP", 500u)]
    [InlineData("RELAY", 2_000u)]  // never the top-level figure
    [InlineData("TUNNEL", 2_000u)]
    public void ATopLevelFigureOf200IsTakenOnlyByALanEndpoint(string transport, uint expected)
    {
        var channel = Channel(MediaKind.Rtsp, Info(200));
        var endpoint = new FastMediaSorterBroadcastEndpoint(
            "rtsp://192.168.1.20:8554/live", transport, "VIDEO_ONLY",
            null, null, null, null, true, null, null, null);
        Assert.Equal(expected, BroadcastLiveCache.For(channel, endpoint));
    }

    [Fact]
    public void AnEndpointsOwnFigureWinsOverTheTopLevelOne()
    {
        var channel = Channel(MediaKind.Rtsp, Info(1_400));
        var endpoint = new FastMediaSorterBroadcastEndpoint(
            "rtsp://192.168.1.20:8554/live", "RTSP", "VIDEO_ONLY",
            null, null, null, null, true, 800, null, null);
        Assert.Equal(800u, BroadcastLiveCache.For(channel, endpoint));
    }

    [Fact]
    public void TheNullEndpointPathIsTheChannelLevelReading()
    {
        var channel = Channel(MediaKind.Rtsp, Info(2_000));
        Assert.Equal(BroadcastLiveCache.For(channel), BroadcastLiveCache.For(channel, null));
    }
}
