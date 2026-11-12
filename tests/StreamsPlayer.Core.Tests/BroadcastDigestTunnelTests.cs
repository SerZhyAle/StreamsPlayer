using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastDigestTunnelTests
{
    [Fact]
    public async Task RtspDigestTravelsInsidePinnedTunnelAndKeepsTheOriginalInnerUri()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var producer = ExchangeTunnelCertificate.Create();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        const string original = "rtsp://192.0.2.1:8554/live";
        var serving = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(token);
            var stream = client.GetStream();
            using var envelope = await ExchangeProtocol.ReadAsync(stream, false, token);
            Assert.Equal("rtsp", ExchangeProtocol.String(envelope.RootElement, "scheme"));
            await ExchangeProtocol.WriteAsync(stream, new { schemaVersion = 2, type = "connected" }, false, token);
            await using var inner = await producer.AcceptAsync(stream, token);
            var request = await DigestRtspMessage.ReadAsync(inner, token);
            var challenge = new BroadcastDigestAuthenticator();
            challenge.SetPairs([new("listener", "tunnel-password")]);
            var challenges = challenge.Check("DESCRIBE", original, null).Challenge;
            await new DigestRtspMessage("RTSP/1.0 401 Unauthorized",
                [new("CSeq", request!.Header("CSeq")!), new("WWW-Authenticate", challenges[0])], []).WriteAsync(inner, token);
            request = await DigestRtspMessage.ReadAsync(inner, token);
            Assert.Equal("DESCRIBE " + original + " RTSP/1.0", request!.StartLine);
            Assert.DoesNotContain("tunnel-password", request.StartLine);
            Assert.NotNull(challenge.Check("DESCRIBE", original, request.Header("Authorization")).Grant);
            await new DigestRtspMessage("RTSP/1.0 200 OK", [new("CSeq", request.Header("CSeq")!)], []).WriteAsync(inner, token);
            Assert.Equal(0, await inner.ReadAsync(new byte[1], token));
        }, token);
        using var forwarder = ExchangeTunnelForwarder.Start("fmsx://exchange.test:44022/b/protected/rtsp", original,
            tunnelPin: producer.Pin, streamConnector: async (_, _, _, ct) =>
            {
                var client = new TcpClient();
                try { await client.ConnectAsync(IPAddress.Loopback, port, ct); return client.GetStream(); }
                catch { client.Dispose(); throw; }
            });
        using var proxy = BroadcastDigestProxy.Start(original, new("listener", "tunnel-password"), connectUrl: forwarder.LoopbackUrl);
        using var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, token);
        await player.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"DESCRIBE {proxy.LoopbackUrl} RTSP/1.0\r\nCSeq: 87\r\n\r\n"), token);
        var response = await DigestRtspMessage.ReadAsync(player.GetStream(), token);
        Assert.Equal("RTSP/1.0 200 OK", response!.StartLine);
        Assert.Equal("87", response.Header("CSeq"));
        Assert.Equal(1, forwarder.ConnectedCount);
        Assert.Null(forwarder.LastRefusalReason);
        Assert.Null(proxy.LastFailure);
        player.Close();
        await serving;
    }
}
