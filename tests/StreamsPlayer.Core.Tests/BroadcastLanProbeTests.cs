using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0250 requirement 4: the LAN reconnect's three answers are told apart before the engine is given the address -
/// a closed port, an ended broadcast's <c>404</c>/<c>410</c>, and everything that is a lost connection or a living server.
/// The servers are real loopback listeners.
/// </summary>
public sealed class BroadcastLanProbeTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    /// <summary>A server that reads one request head and writes <paramref name="answer"/> (or nothing, or resets).</summary>
    private sealed class Responder : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }
        public string? Request { get; private set; }

        public Responder(string? answer, bool reset = false)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[2048];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    Request = Encoding.ASCII.GetString(buffer, 0, read);
                    if (reset)
                    {
                        client.LingerState = new LingerOption(true, 0);
                        return;
                    }

                    if (answer is not null)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
                    }

                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task AClosedPortIsRefused()
    {
        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"http://127.0.0.1:{ClosedPort()}/live.ts"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.LanRefused, answer);
    }

    [Fact]
    public async Task AnRtspDescribeAnsweredNotFoundIsAnEndedBroadcast()
    {
        using var server = new Responder("RTSP/1.0 404 Not Found\r\nCSeq: 1\r\n\r\n");

        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"rtsp://127.0.0.1:{server.Port}/live"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.LanNotFound, answer);
        Assert.StartsWith("DESCRIBE rtsp://127.0.0.1:", server.Request);
        Assert.Contains("RTSP/1.0", server.Request);
    }

    [Fact]
    public async Task AnHttpGetAnsweredGoneIsAnEndedBroadcast()
    {
        using var server = new Responder("HTTP/1.1 410 Gone\r\nContent-Length: 0\r\n\r\n");

        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"http://127.0.0.1:{server.Port}/live.ts"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.LanGone, answer);
        Assert.StartsWith("GET /live.ts HTTP/1.1", server.Request);
    }

    [Fact]
    public async Task AnHttpGetAnsweredNotFoundIsAnEndedBroadcast()
    {
        using var server = new Responder("HTTP/1.1 404 Not Found\r\n\r\n");

        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"http://127.0.0.1:{server.Port}/x"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.LanNotFound, answer);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\n\r\n")]
    [InlineData("RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n")]
    [InlineData("RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n\r\n")]
    [InlineData("HTTP/1.1 503 Service Unavailable\r\n\r\n")]
    [InlineData("not a status line at all\r\n")]
    public async Task AServerThatIsAliveOrUnintelligibleIsNoAnswer(string response)
    {
        using var server = new Responder(response);
        var scheme = response.StartsWith("RTSP", StringComparison.Ordinal) ? "rtsp" : "http";

        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"{scheme}://127.0.0.1:{server.Port}/live"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.None, answer);
    }

    [Fact]
    public async Task AServerThatNeverWritesIsNoAnswer()
    {
        using var server = new Responder(answer: null);

        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"http://127.0.0.1:{server.Port}/live"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.None, answer);
    }

    [Fact]
    public async Task AResetAfterTheConnectionIsNoAnswer()
    {
        using var server = new Responder(answer: null, reset: true);

        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"http://127.0.0.1:{server.Port}/live"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.None, answer);
    }

    [Fact]
    public async Task AnHttpsAddressIsOnlyConnectedTo()
    {
        using var server = new Responder(answer: null);

        var open = await BroadcastLanProbe.ProbeAsync(new Uri($"https://127.0.0.1:{server.Port}/live"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);
        var closed = await BroadcastLanProbe.ProbeAsync(new Uri($"https://127.0.0.1:{ClosedPort()}/live"),
            CancellationToken.None, TimeSpan.FromSeconds(3), Short);

        Assert.Equal(BroadcastEndAnswer.None, open);
        Assert.Equal(BroadcastEndAnswer.LanRefused, closed);
    }

    [Fact]
    public async Task AnAddressOfAnotherSchemeIsNeverProbed()
    {
        var answer = await BroadcastLanProbe.ProbeAsync(new Uri("fmsx://server/b/id/rtsp"), CancellationToken.None);

        Assert.Equal(BroadcastEndAnswer.None, answer);
    }

    [Fact]
    public async Task ACancelledProbeStopsInsteadOfAnswering()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BroadcastLanProbe.ProbeAsync(
            new Uri($"http://127.0.0.1:{ClosedPort()}/live"), cancelled.Token));
    }

    [Theory]
    [InlineData("RTSP/1.0 404 Not Found", 404)]
    [InlineData("HTTP/1.1 200 OK", 200)]
    [InlineData("HTTP/1.0 410", 410)]
    [InlineData("HTTP/1.1 4040 x", null)]
    [InlineData("SMTP 404 no", null)]
    [InlineData("", null)]
    public void TheStatusCodeIsReadOffTheFirstLineOnly(string line, int? code)
    {
        Assert.Equal(code, BroadcastLanProbe.StatusOf(line));
    }
}
