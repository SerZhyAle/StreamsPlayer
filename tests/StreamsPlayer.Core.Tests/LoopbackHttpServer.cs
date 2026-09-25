using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0129: a real socket on 127.0.0.1 that answers one request with a scripted behaviour. A stub
/// <see cref="HttpMessageHandler"/> cannot prove a deadline, because the stall it simulates is not the
/// stall the socket handler sees; only a real connection exercises the same code the product runs.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serving;

    private LoopbackHttpServer(Func<NetworkStream, CancellationToken, Task> respond)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/resource";
        _serving = Task.Run(() => ServeAsync(respond, _lifetime.Token));
    }

    public string Url { get; }

    /// <summary>Accepts the connection, reads the request, and never answers.</summary>
    public static LoopbackHttpServer SilentBeforeHeaders() =>
        new(async (_, token) => await Task.Delay(Timeout.Infinite, token));

    /// <summary>Sends the head and <paramref name="firstBytes"/> of a longer body, then holds the socket open.</summary>
    public static LoopbackHttpServer StallAfterHeaders(string contentType, int declaredLength, int firstBytes) =>
        new(async (stream, token) =>
        {
            await WriteHeadAsync(stream, contentType, declaredLength, extraHeaders: null, token);
            await stream.WriteAsync(new byte[firstBytes], token);
            await stream.FlushAsync(token);
            await Task.Delay(Timeout.Infinite, token);
        });

    /// <summary>Sends a head, then a body of <paramref name="bodyBytes"/> without declaring its length.</summary>
    public static LoopbackHttpServer Body(string contentType, int bodyBytes, string? extraHeaders = null) =>
        new(async (stream, token) =>
        {
            await WriteHeadAsync(stream, contentType, declaredLength: null, extraHeaders, token);
            var chunk = Encoding.ASCII.GetBytes(new string('#', 16 * 1024) + "\n");
            for (var sent = 0; sent < bodyBytes; sent += chunk.Length)
            {
                await stream.WriteAsync(chunk, token);
            }

            await stream.FlushAsync(token);
        });

    /// <summary>Sends a complete, declared body.</summary>
    public static LoopbackHttpServer Document(string contentType, string body) =>
        new(async (stream, token) =>
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            await WriteHeadAsync(stream, contentType, bytes.Length, extraHeaders: null, token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
        });

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        try
        {
            await _serving;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException
                                              or ObjectDisposedException)
        {
            // The client gave up first, which is what these tests arrange.
        }

        _lifetime.Dispose();
    }

    private async Task ServeAsync(Func<NetworkStream, CancellationToken, Task> respond, CancellationToken token)
    {
        using var client = await _listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        await DrainRequestHeadAsync(stream, token);
        await respond(stream, token);
    }

    private static async Task DrainRequestHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var received = new StringBuilder();
        var buffer = new byte[1024];
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0)
            {
                return;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    private static async Task WriteHeadAsync(
        NetworkStream stream, string contentType, int? declaredLength, string? extraHeaders, CancellationToken token)
    {
        var head = new StringBuilder("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append(declaredLength is null ? "Connection: close\r\n" : $"Content-Length: {declaredLength}\r\n")
            .Append(extraHeaders)
            .Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token);
        await stream.FlushAsync(token);
    }
}
