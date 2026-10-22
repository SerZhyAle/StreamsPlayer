using System.Net;
using System.Net.Sockets;
using StreamsPlayer.App;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// DEVICE-EXCHANGE 7.5 item L: a producer's tunnel bridge is cut after ten minutes with no byte in either direction -
/// idle, not old. The pump is driven here with a short timeout over real sockets.
/// </summary>
public sealed class ExchangeTunnelBridgeTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(200);

    private static async Task<(NetworkStream Peer, NetworkStream Bridged)> PairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var bridged = await accepting;
        return (peer.GetStream(), bridged.GetStream());
    }

    [Fact]
    public void TheIdleTimeoutIsTenMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), ExchangeTunnelBridge.IdleTimeout);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABridgeThatKeepsMovingBytesIsNotCutAtAFixedAge(bool fromRemote)
    {
        var (remotePeer, remoteBridged) = await PairAsync();
        var (localPeer, localBridged) = await PairAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pump = ExchangeTunnelBridge.PumpAsync(remoteBridged, localBridged, Idle, cancellation.Token);
        var sender = fromRemote ? remotePeer : localPeer;
        var receiver = fromRemote ? localPeer : remotePeer;

        // Eight chunks over 1.6 s: far past the 0.7 s timeout, never a gap as long as it.
        var buffer = new byte[1];
        for (var chunk = 0; chunk < 8; chunk++)
        {
            await sender.WriteAsync(new byte[] { (byte)chunk });
            Assert.Equal(1, await receiver.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal((byte)chunk, buffer[0]);
            Assert.False(pump.IsCompleted, $"the bridge was cut with traffic still moving (chunk {chunk})");
            await Task.Delay(Gap);
        }

        // Silence: the same timeout now ends it, on its own.
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var stream in new[] { remotePeer, localPeer, remoteBridged, localBridged })
        {
            stream.Dispose();
        }
    }

    [Fact]
    public async Task ABridgeWithNoTrafficAtAllEndsAtTheTimeout()
    {
        var (remotePeer, remoteBridged) = await PairAsync();
        var (localPeer, localBridged) = await PairAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var started = System.Diagnostics.Stopwatch.StartNew();

        await ExchangeTunnelBridge.PumpAsync(remoteBridged, localBridged, Idle, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.InRange(started.Elapsed, Idle - TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        foreach (var stream in new[] { remotePeer, localPeer, remoteBridged, localBridged })
        {
            stream.Dispose();
        }
    }
}
