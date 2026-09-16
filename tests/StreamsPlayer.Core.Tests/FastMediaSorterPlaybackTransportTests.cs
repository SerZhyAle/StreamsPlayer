using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

public sealed class FastMediaSorterPlaybackTransportTests
{
    [Fact]
    public async Task OpenAsync_UsesOneGetAndPublishesCleanEofWithoutIcyMetadata()
    {
        await using var server = new PlaybackServer(["HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: audio/aac\r\n\r\n3\r\nAAC\r\n0\r\n\r\n"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new FastMediaSorterPlaybackTransport();

        using var connection = await transport.OpenAsync(server.Endpoint, cancellation.Token);
        Assert.Equal(200, connection.StatusCode);
        Assert.NotNull(connection.Stream);
        using var body = new MemoryStream();
        await connection.Stream!.CopyToAsync(body, cancellation.Token);

        Assert.Equal("AAC", Encoding.ASCII.GetString(body.ToArray()));
        Assert.True(connection.EndOfStream);
        Assert.Null(connection.TransportError);
        Assert.Equal(1, await server.RequestCountAsync(cancellation.Token));
        Assert.DoesNotContain("Icy-MetaData", server.Requests[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_WatchSocketCloseWithoutTerminatorIsEndOfStreamOnOneRequest()
    {
        await using var server = new PlaybackServer(["HTTP/1.0 200 OK\r\nContent-Type: audio/aac\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\nAAC"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var connection = await new FastMediaSorterPlaybackTransport().OpenAsync(server.Endpoint, cancellation.Token);
        Assert.Equal(200, connection.StatusCode);
        using var body = new MemoryStream();
        await connection.Stream!.CopyToAsync(body, cancellation.Token);

        Assert.Equal("AAC", Encoding.ASCII.GetString(body.ToArray()));
        Assert.True(connection.EndOfStream);
        Assert.Equal(1, await server.RequestCountAsync(cancellation.Token));
    }

    [Fact]
    public async Task OpenAsync_Publishes503FromTheOnlyRequest()
    {
        await using var server = new PlaybackServer(["HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var connection = await new FastMediaSorterPlaybackTransport().OpenAsync(server.Endpoint, cancellation.Token);

        Assert.Equal(503, connection.StatusCode);
        Assert.Null(connection.Stream);
        Assert.Null(connection.TransportError);
        Assert.Equal(1, await server.RequestCountAsync(cancellation.Token));
    }

    [Fact]
    public async Task OpenAsync_ReconnectCreatesExactlyOneNewLeg()
    {
        await using var server = new PlaybackServer(
        [
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\nA",
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\nB"
        ]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new FastMediaSorterPlaybackTransport();

        using (var first = await transport.OpenAsync(server.Endpoint, cancellation.Token))
        {
            Assert.Equal(200, first.StatusCode);
            Assert.Equal('A', (char)first.Stream!.ReadByte());
        }

        using (var second = await transport.OpenAsync(server.Endpoint, cancellation.Token))
        {
            Assert.Equal(200, second.StatusCode);
            Assert.Equal('B', (char)second.Stream!.ReadByte());
        }

        Assert.Equal(2, await server.RequestCountAsync(cancellation.Token));
    }

    private sealed class PlaybackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;
        private readonly List<string> _requests = [];

        public PlaybackServer(IReadOnlyList<string> responses)
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/live-audio.aac");
            _serve = ServeAsync(responses);
        }

        public Uri Endpoint { get; }
        public IReadOnlyList<string> Requests => _requests;

        public async Task<int> RequestCountAsync(CancellationToken cancellationToken)
        {
            await _serve.WaitAsync(cancellationToken);
            return _requests.Count;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (SocketException)
            {
                // Stopping a listener that is waiting for a test request is normal teardown.
            }
        }

        private async Task ServeAsync(IReadOnlyList<string> responses)
        {
            foreach (var response in responses)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                _requests.Add(await ReadRequestAsync(stream));
                var bytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return Encoding.ASCII.GetString([.. bytes]);
                }

                bytes.Add(buffer[0]);
                if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                {
                    return Encoding.ASCII.GetString([.. bytes]);
                }
            }
        }
    }
}
