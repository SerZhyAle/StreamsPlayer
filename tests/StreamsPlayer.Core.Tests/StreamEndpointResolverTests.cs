using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamEndpointResolverTests
{
    [Theory]
    [InlineData("http://radio.example.com/stream", "radio.example.com", 80)]
    [InlineData("https://radio.example.com/live.m3u8", "radio.example.com", 443)]
    [InlineData("http://radio.example.com:8000/stream", "radio.example.com", 8000)]
    [InlineData("rtsp://cam.example.com/h264", "cam.example.com", 554)]
    [InlineData("rtsp://cam.example.com:8554/h264", "cam.example.com", 8554)]
    [InlineData("HTTPS://Radio.Example.com/x", "radio.example.com", 443)]
    public void TryResolve_ReadsHostAndPort(string url, string host, int port)
    {
        var endpoint = StreamEndpointResolver.TryResolve(url);
        Assert.NotNull(endpoint);
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.False(endpoint.IsLocal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Radio Paradise")]
    [InlineData("C:\\music\\stream.m3u")]
    [InlineData("file:///C:/music/stream.m3u")]
    [InlineData("mms://radio.example.com/stream")]
    [InlineData("rtmp://live.example.com/app")]
    public void TryResolve_RefusesWhatIsNeverLaunched(string? url)
    {
        // An address no engine is handed is never probed, and a port is never guessed for one.
        Assert.Null(StreamEndpointResolver.TryResolve(url));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/x")]
    [InlineData("http://localhost/x")]
    [InlineData("http://LOCALHOST:9000/x")]
    [InlineData("http://10.1.2.3/x")]
    [InlineData("http://172.16.0.1/x")]
    [InlineData("http://172.31.255.254/x")]
    [InlineData("rtsp://192.168.1.20:554/dead")]
    [InlineData("http://169.254.10.10/x")]
    // SP-0168: 100.64.0.0/10 - carrier-grade NAT and overlay networks such as Tailscale.
    [InlineData("http://100.64.0.1/x")]
    [InlineData("http://100.101.102.103/x")]
    [InlineData("http://100.127.255.254/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://[fe80::1]/x")]
    [InlineData("http://[fd12:3456::1]/x")]
    [InlineData("rtsp://camera/stream")]
    [InlineData("http://nas.local/x")]
    [InlineData("http://router.lan/x")]
    [InlineData("http://box.home/x")]
    [InlineData("http://svc.internal/x")]
    public void TryResolve_MarksLocalAddresses(string url)
    {
        var endpoint = StreamEndpointResolver.TryResolve(url);
        Assert.NotNull(endpoint);
        Assert.True(endpoint.IsLocal);
    }

    [Theory]
    [InlineData("http://8.8.8.8/x")]
    [InlineData("http://172.15.0.1/x")]
    [InlineData("http://172.32.0.1/x")]
    [InlineData("http://192.169.1.1/x")]
    [InlineData("http://100.63.255.255/x")]
    [InlineData("http://100.128.0.1/x")]
    [InlineData("http://[2001:db8::1]/x")]
    [InlineData("https://stream.example.org/live")]
    [InlineData("https://local.example.org/live")]
    public void TryResolve_LeavesPublicAddressesNonLocal(string url)
    {
        var endpoint = StreamEndpointResolver.TryResolve(url);
        Assert.NotNull(endpoint);
        Assert.False(endpoint.IsLocal);
    }

    [Fact]
    public void TryResolve_StripsIpv6Brackets()
    {
        // The probe hands this string to a socket, which wants the bare literal.
        Assert.Equal("fe80::1", StreamEndpointResolver.TryResolve("http://[fe80::1]:8080/x")?.Host);
    }
}
