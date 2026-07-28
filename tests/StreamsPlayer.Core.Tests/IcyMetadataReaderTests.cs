using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class IcyMetadataReaderTests
{
    [Fact]
    public async Task ReadAsync_ReportsChangedStreamTitlesFromIcyStreamInOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => RunIcyServerAsync(listener, cts.Token), cts.Token);

        var recorder = new TitleRecorder(expected: 2);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new IcyMetadataReader(client);
        var readTask = reader.ReadAsync($"http://127.0.0.1:{port}/", recorder, cts.Token);

        await recorder.Expected.WaitAsync(TimeSpan.FromSeconds(12), cts.Token);
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

        Assert.Equal(["Test Artist - Test Song", "Second Track"], recorder.Titles);
    }

    [Fact]
    public async Task ReadAsync_WithoutMetaIntHeaderReportsNothing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => RunPlainServerAsync(listener, cts.Token), cts.Token);

        var recorder = new TitleRecorder(expected: 1);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new IcyMetadataReader(client);

        // No icy-metaint header: the reader must return cleanly without reporting.
        await reader.ReadAsync($"http://127.0.0.1:{port}/", recorder, cts.Token);
        listener.Stop();

        Assert.Empty(recorder.Titles);
    }

    /// <summary>
    /// SP-0036: records reported titles in the order the reader produced them.
    /// <para>
    /// These tests used <see cref="Progress{T}"/>, whose contract is to <em>post</em> each callback - with
    /// no ambient <see cref="SynchronizationContext"/> that means one thread-pool work item per report,
    /// and two work items can run in either order. The reader itself reports strictly in sequence inside
    /// one loop, so the sequence was only ever lost in the observer: the assertion on the first title
    /// occasionally saw the second one. The application is unaffected - it builds its
    /// <see cref="Progress{T}"/> on the UI thread, where posts run in dispatcher order.
    /// </para>
    /// <para>
    /// Implementing <see cref="IProgress{T}"/> directly makes <c>Report</c> run inline on the reader's
    /// thread, so the recorded list is the reader's own order by construction rather than by timing.
    /// </para>
    /// </summary>
    private sealed class TitleRecorder(int expected) : IProgress<string?>
    {
        private readonly object _sync = new();
        private readonly List<string?> _titles = [];
        private readonly TaskCompletionSource _expected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the expected number of titles has been reported.</summary>
        internal Task Expected => _expected.Task;

        internal IReadOnlyList<string?> Titles
        {
            get
            {
                lock (_sync)
                {
                    return [.. _titles];
                }
            }
        }

        public void Report(string? value)
        {
            lock (_sync)
            {
                _titles.Add(value);
                if (_titles.Count >= expected)
                {
                    _expected.TrySetResult();
                }
            }
        }
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
