using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeTunnelUrlTests
{
    [Theory]
    [InlineData("fmsx://exchange.example.net:44022/b/ICEiIyQlJicoKSorLC0uLw/http", "exchange.example.net", 44022, "ICEiIyQlJicoKSorLC0uLw", "http")]
    [InlineData("fmsx://127.0.0.1/b/b1/rtsp", "127.0.0.1", 44022, "b1", "rtsp")]
    [InlineData("FMSX://LOCALHOST:5000/B/BROADCAST-1/HTTP", "localhost", 5000, "BROADCAST-1", "http")]
    public void ValidFmsxUrlsAreParsed(string raw, string expectedHost, int expectedPort, string expectedBroadcastId, string expectedScheme)
    {
        Assert.True(ExchangeTunnelUrl.TryParse(raw, out var tunnelUrl));
        Assert.NotNull(tunnelUrl);
        Assert.Equal(expectedHost, tunnelUrl.Host, ignoreCase: true);
        Assert.Equal(expectedPort, tunnelUrl.Port);
        Assert.Equal(expectedBroadcastId, tunnelUrl.BroadcastId);
        Assert.Equal(expectedScheme, tunnelUrl.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://exchange.example.net:44022/b/b1/http")]
    [InlineData("fmsx://")]
    [InlineData("fmsx://exchange.example.net:44022")]
    [InlineData("fmsx://exchange.example.net:44022/b")]
    [InlineData("fmsx://exchange.example.net:44022/b/b1")]
    [InlineData("fmsx://exchange.example.net:44022/b/b1/ftp")]
    [InlineData("fmsx://exchange.example.net:44022/x/b1/http")]
    [InlineData("fmsx://exchange.example.net:44022/b/b1/http/extra")]
    public void InvalidFmsxUrlsAreRefused(string? raw)
    {
        Assert.False(ExchangeTunnelUrl.TryParse(raw, out var tunnelUrl));
        Assert.Null(tunnelUrl);
    }

    [Fact]
    public void RewriteInnerReplacesHostAndPortWhilePreservingPathAndQuery()
    {
        var rewritten = ExchangeTunnelUrl.RewriteInner("http://192.168.1.97:8768/live-audio.aac?token=secret123", 54321);
        Assert.Equal("http://127.0.0.1:54321/live-audio.aac?token=secret123", rewritten);

        var rewrittenRtsp = ExchangeTunnelUrl.RewriteInner("rtsp://192.0.2.1:8554/live", 54321);
        Assert.Equal("rtsp://127.0.0.1:54321/live", rewrittenRtsp);
    }

    [Fact]
    public void RewriteInnerThrowsOnInvalidAddress()
    {
        Assert.Throws<ArgumentException>(() => ExchangeTunnelUrl.RewriteInner("ftp://192.168.1.97/live", 54321));
        Assert.Throws<ArgumentException>(() => ExchangeTunnelUrl.RewriteInner("not-a-url", 54321));
    }
}
