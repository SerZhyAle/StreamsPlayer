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
    // The idle bound of a bridge nothing is sent through; short, because the test only waits for it to end the pump.
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(700);

    // The idle bound of a bridge that keeps moving bytes, and the gap between those bytes. Task.Delay is a floor, never a
    // ceiling, so a loaded machine stretches the gap and cannot stretch the duration below its floor: the gap is a twentieth
    // of the bound, and the chunks together last longer than the bound. (A 200 ms gap against 0.7 s was a 3.5x margin.)
    private static readonly TimeSpan LiveIdle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(100);
    private const int Chunks = 25;

    // How long an ending pump may take before the test calls the idle bound broken; the pump has no other end here.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

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
        var pump = ExchangeTunnelBridge.PumpAsync(remoteBridged, localBridged, LiveIdle, cancellation.Token);
        var sender = fromRemote ? remotePeer : localPeer;
        var receiver = fromRemote ? localPeer : remotePeer;

        // Chunks over 2.5 s: past the 2 s timeout, never a gap as long as it.
        var buffer = new byte[1];
        for (var chunk = 0; chunk < Chunks; chunk++)
        {
            await sender.WriteAsync(new byte[] { (byte)chunk });
            Assert.Equal(1, await receiver.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal((byte)chunk, buffer[0]);
            Assert.False(pump.IsCompleted, $"the bridge was cut with traffic still moving (chunk {chunk})");
            await Task.Delay(Gap);
        }

        // Silence: the same timeout now ends it, on its own.
        await pump.WaitAsync(TimeSpan.FromSeconds(30));
        foreach (var stream in new[] { remotePeer, localPeer, remoteBridged, localBridged })
        {
            stream.Dispose();
        }
    }

    // SP-0263 (S3-3): a reset on one side used to leave the other copy waiting out the whole idle timeout.
    [Fact]
    public async Task AResetOnOneSideEndsTheBridgeWithoutWaitingForTheIdleTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        using var resetPeer = new TcpClient();
        await resetPeer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var resetBridged = await accepting;
        var (otherPeer, otherBridged) = await PairAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pump = ExchangeTunnelBridge.PumpAsync(resetBridged.GetStream(), otherBridged, TimeSpan.FromMinutes(10), cancellation.Token);

        resetPeer.Client.LingerState = new LingerOption(true, 0);
        resetPeer.Close();

        await pump.WaitAsync(TimeSpan.FromSeconds(30));
        otherPeer.Dispose();
        otherBridged.Dispose();
    }

    [Fact]
    public async Task ABridgeWithNoTrafficAtAllEndsAtTheTimeout()
    {
        var (remotePeer, remoteBridged) = await PairAsync();
        var (localPeer, localBridged) = await PairAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var started = System.Diagnostics.Stopwatch.StartNew();

        await ExchangeTunnelBridge.PumpAsync(remoteBridged, localBridged, Idle, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.InRange(started.Elapsed, Idle - TimeSpan.FromMilliseconds(100), Limit);
        foreach (var stream in new[] { remotePeer, localPeer, remoteBridged, localBridged })
        {
            stream.Dispose();
        }
    }
}
