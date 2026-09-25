using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0131: every <see cref="IcyReadOutcome"/> the reader can return, and the Shoutcast v1 socket fallback
/// of SP-0074, each against a loopback server scripted to produce it.
/// </summary>
public sealed class IcyReadOutcomeTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(20);
    private static readonly Encoding Windows1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;

    [Fact]
    public async Task IcyGreetingFallsBackToTheSocketAndReportsTitles()
    {
        using var server = LoopbackServer.Start(async (stream, token) =>
        {
            await WriteAsciiAsync(stream, "ICY 200 OK\r\nicy-metaint: 16\r\n\r\n", token);
            await stream.WriteAsync(new byte[16], token);
            await WriteBlockAsync(stream, Windows1251.GetBytes("StreamTitle='Кино - Звезда';"), token);
            await stream.WriteAsync(new byte[16], token);
            await Task.Delay(Timeout.Infinite, token);
        });
        using var cts = new CancellationTokenSource(TestDeadline);
        var recorder = new TitleRecorder();
        var reader = NewReader();

        var read = reader.ReadAsync(server.Url, recorder, cts.Token);
        await recorder.First.WaitAsync(TestDeadline);
        cts.Cancel();

        Assert.Equal(IcyReadOutcome.TitlesReported, await read);
        Assert.Equal(["Кино - Звезда"], recorder.Titles);
        Assert.Equal(IcyTextEncoding.Windows1251, reader.TextEncoding);
        Assert.Equal(2, server.Connections); // the refused HTTP attempt, then the socket
    }

    [Fact]
    public async Task NonSuccessIcyGreetingOnTheSocketIsMalformed()
    {
        using var server = LoopbackServer.Start((stream, token) =>
            WriteAsciiAsync(stream, "ICY 404 Resource Not Found\r\n\r\n", token));

        Assert.Equal(IcyReadOutcome.Malformed, await ReadAsync(server.Url));
    }

    [Fact]
    public async Task NonSuccessHttpAnswerIsUnreachable()
    {
        using var server = LoopbackServer.Start((stream, token) =>
            WriteAsciiAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token));

        Assert.Equal(IcyReadOutcome.Unreachable, await ReadAsync(server.Url));
    }

    [Fact]
    public async Task TheSocketPathRefusesHttps()
    {
        using var cts = new CancellationTokenSource(TestDeadline);

        var outcome = await NewReader().ReadViaSocketAsync(
            "https://127.0.0.1:1/live", new TitleRecorder(), cts.Token, cts.Token);

        Assert.Equal(IcyReadOutcome.StatusLineRefused, outcome);
    }

    [Fact]
    public async Task AnswerWithoutMetaIntIsNoMetadataOffered()
    {
        using var server = LoopbackServer.Start(async (stream, token) =>
        {
            await WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nConnection: close\r\n\r\n", token);
            await stream.WriteAsync(new byte[64], token);
        });

        Assert.Equal(IcyReadOutcome.NoMetadataOffered, await ReadAsync(server.Url));
    }

    [Fact]
    public async Task StreamEndingBeforeAnyTitleIsStreamEnded()
    {
        using var server = LoopbackServer.Start(async (stream, token) =>
        {
            await WriteAsciiAsync(stream, MetaIntHead, token);
            await stream.WriteAsync(new byte[8], token);
        });

        Assert.Equal(IcyReadOutcome.StreamEnded, await ReadAsync(server.Url));
    }

    [Fact]
    public async Task StreamEndingAfterATitleIsTitlesReported()
    {
        using var server = LoopbackServer.Start(async (stream, token) =>
        {
            await WriteAsciiAsync(stream, MetaIntHead, token);
            await stream.WriteAsync(new byte[16], token);
            await WriteBlockAsync(stream, Encoding.UTF8.GetBytes("StreamTitle='Beyoncé - Halo';"), token);
        });
        var recorder = new TitleRecorder();
        var reader = NewReader();
        using var cts = new CancellationTokenSource(TestDeadline);

        Assert.Equal(IcyReadOutcome.TitlesReported, await reader.ReadAsync(server.Url, recorder, cts.Token));
        Assert.Equal(["Beyoncé - Halo"], recorder.Titles);
        Assert.Equal(IcyTextEncoding.Utf8, reader.TextEncoding);
    }

    [Fact]
    public async Task CallerCancellationBeforeAnyTitleIsCancelled()
    {
        using var server = LoopbackServer.Start(async (stream, token) =>
        {
            await WriteAsciiAsync(stream, MetaIntHead, token);
            await Task.Delay(Timeout.Infinite, token);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var outcome = await NewReader().ReadAsync(server.Url, new TitleRecorder(), cts.Token);

        Assert.Equal(IcyReadOutcome.Cancelled, outcome);
    }

    [Fact]
    public async Task RefusedConnectionIsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        // Windows retries a refused loopback SYN for about two seconds before reporting it, so this
        // attempt needs a deadline longer than that or it reads as a timeout.
        var reader = new IcyMetadataReader(SharedClient, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource(TestDeadline);

        Assert.Equal(
            IcyReadOutcome.Unreachable,
            await reader.ReadAsync($"http://127.0.0.1:{port}/", new TitleRecorder(), cts.Token));
    }

    [Fact]
    public async Task ServerThatNeverAnswersIsTimedOut()
    {
        using var server = LoopbackServer.Start((_, token) => Task.Delay(Timeout.Infinite, token));

        Assert.Equal(IcyReadOutcome.TimedOut, await ReadAsync(server.Url));
    }

    [Fact]
    public async Task StreamThatFallsSilentIsTimedOut()
    {
        using var server = LoopbackServer.Start(async (stream, token) =>
        {
            await WriteAsciiAsync(stream, MetaIntHead, token);
            await Task.Delay(Timeout.Infinite, token);
        });

        Assert.Equal(IcyReadOutcome.TimedOut, await ReadAsync(server.Url));
    }

    private const string MetaIntHead =
        "HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nicy-metaint: 16\r\nConnection: close\r\n\r\n";

    private static IcyMetadataReader NewReader() =>
        new(SharedClient, connectTimeout: TimeSpan.FromSeconds(1), silenceTimeout: TimeSpan.FromSeconds(1));

    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<IcyReadOutcome> ReadAsync(string url)
    {
        using var cts = new CancellationTokenSource(TestDeadline);
        return await NewReader().ReadAsync(url, new TitleRecorder(), cts.Token);
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    private static async Task WriteBlockAsync(Stream stream, byte[] text, CancellationToken token)
    {
        var blocks = (text.Length + 15) / 16;
        var padded = new byte[blocks * 16];
        text.CopyTo(padded, 0);
        await stream.WriteAsync(new[] { (byte)blocks }, token);
        await stream.WriteAsync(padded, token);
    }

    /// <summary>Records titles inline, in the reader's own order (see <c>IcyMetadataReaderTests</c>).</summary>
    private sealed class TitleRecorder : IProgress<string?>
    {
        private readonly object _sync = new();
        private readonly List<string?> _titles = [];
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task First => _first.Task;

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
            }

            _first.TrySetResult();
        }
    }

    /// <summary>Accepts every connection and runs the same script on it after draining the request head.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private int _connections;

        private LoopbackServer(TcpListener listener) => _listener = listener;

        internal string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/live";

        internal int Connections => Volatile.Read(ref _connections);

        internal static LoopbackServer Start(Func<NetworkStream, CancellationToken, Task> script)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new LoopbackServer(listener);
            _ = Task.Run(() => server.AcceptLoopAsync(script));
            return server;
        }

        private async Task AcceptLoopAsync(Func<NetworkStream, CancellationToken, Task> script)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            _ = await stream.ReadAsync(new byte[4096], _stop.Token);
                            await script(stream, _stop.Token);
                        }
                        catch
                        {
                            // The reader hung up or the test ended; either is the script's natural end.
                        }
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
