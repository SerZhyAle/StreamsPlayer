using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0212 (L26): the tunnel's connect phase - the TLS authenticate, the connect envelope, its answer -
/// carries a deadline of its own, because the caller's token outlives it by design and a stalled server
/// would otherwise hold the leg's watcher on a connect that never finishes. The stream connector is the
/// forwarder's own seam, so the stalled connect here is the one the loopback path actually sees, on the
/// terms of <c>NetworkDeadlineTests</c>: a short bound, and a limit no unbounded path could meet.
/// </summary>
public sealed class ExchangeTunnelForwarderTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AStalledConnectEndsWithinTheConnectDeadline()
    {
        var previous = ExchangeTunnelForwarder.ConnectDeadline;
        ExchangeTunnelForwarder.ConnectDeadline = Deadline;
        try
        {
            using var forwarder = ExchangeTunnelForwarder.Start(
                "fmsx://exchange.example.net:44022/b/b1/http",
                "http://192.168.1.97:8768/live-audio.aac",
                certFingerprint: null,
                maxConnections: 1,
                // The connector honours the token the way the production TLS connect does - its connect
                // phase is cancelled, never left hanging on an ignored deadline.
                streamConnector: (_, _, _, token) =>
                    new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token));

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
            using var stream = client.GetStream();
            var clock = Stopwatch.StartNew();
            var read = await stream.ReadAsync(new byte[16]);
            Assert.Equal(0, read); // the refused-under-deadline connect closes the client side
            Assert.True(clock.Elapsed < Limit, $"the connect took {clock.Elapsed} to give up; the deadline is {Deadline}");
            Assert.Equal(1, forwarder.ConnectAttempts);
            Assert.Equal(0, forwarder.ConnectedCount);
        }
        finally
        {
            ExchangeTunnelForwarder.ConnectDeadline = previous;
        }
    }
}
