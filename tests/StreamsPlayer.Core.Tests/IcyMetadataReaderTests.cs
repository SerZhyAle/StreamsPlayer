using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class IcyMetadataReaderTests
{
    [Fact]
    public async Task ReadAsync_ReportsChangedStreamTitlesFromIcyStream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => RunIcyServerAsync(listener, cts.Token), cts.Token);

        var sync = new object();
        var reported = new List<string?>();
        var gotBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Progress<string?>(title =>
        {
            lock (sync)
            {
                reported.Add(title);
                if (reported.Count >= 2)
                {
                    gotBoth.TrySetResult();
                }
            }
        });

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new IcyMetadataReader(client);
        var readTask = reader.ReadAsync($"http://127.0.0.1:{port}/", progress, cts.Token);

        await Task.WhenAny(gotBoth.Task, Task.Delay(TimeSpan.FromSeconds(12), cts.Token));
        cts.Cancel();
        try
        {
            await readTask;
        }
        catch
        {
            // Reader swallows its own exceptions; nothing to observe here.
        }

        listener.Stop();

        lock (sync)
        {
            Assert.Equal(2, reported.Count);
            Assert.Equal("Test Artist - Test Song", reported[0]);
            Assert.Equal("Second Track", reported[1]);
        }
    }

    [Fact]
    public async Task ReadAsync_WithoutMetaIntHeaderReportsNothing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => RunPlainServerAsync(listener, cts.Token), cts.Token);

        var reportedAny = false;
        var progress = new Progress<string?>(_ => reportedAny = true);

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new IcyMetadataReader(client);

        // No icy-metaint header: the reader must return cleanly without reporting.
        await reader.ReadAsync($"http://127.0.0.1:{port}/", progress, cts.Token);
        listener.Stop();

        Assert.False(reportedAny);
    }

    private static async Task RunIcyServerAsync(TcpListener listener, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        await DrainRequestAsync(stream, token);

        const string header = "HTTP/1.1 200 OK\r\n" +
                              "Content-Type: audio/mpeg\r\n" +
                              "icy-metaint: 16\r\n" +
                              "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);

        await stream.WriteAsync(new byte[16], token); // audio segment before first metadata
        await WriteMetadataBlockAsync(stream, "StreamTitle='Test Artist - Test Song';", token);
        await stream.WriteAsync(new byte[16], token);
        await WriteMetadataBlockAsync(stream, "StreamTitle='Second Track';", token);
        await stream.WriteAsync(new byte[16], token); // trailing audio so the reader loops past block 2
        await stream.FlushAsync(token);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the test cancels once both titles are observed.
        }
    }

    private static async Task RunPlainServerAsync(TcpListener listener, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        await DrainRequestAsync(stream, token);

        const string header = "HTTP/1.1 200 OK\r\n" +
                              "Content-Type: audio/mpeg\r\n" +
                              "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
        await stream.WriteAsync(new byte[64], token);
        await stream.FlushAsync(token);
    }

    private static async Task DrainRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer, token);
    }

    private static async Task WriteMetadataBlockAsync(NetworkStream stream, string title, CancellationToken token)
    {
        var text = Encoding.ASCII.GetBytes(title);
        var blocks = (text.Length + 15) / 16;
        var padded = new byte[blocks * 16];
        Array.Copy(text, padded, text.Length);
        await stream.WriteAsync(new[] { (byte)blocks }, token);
        await stream.WriteAsync(padded, token);
    }
}
