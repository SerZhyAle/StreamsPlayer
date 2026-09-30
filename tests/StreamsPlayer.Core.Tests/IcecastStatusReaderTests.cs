using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0172: a status endpoint that has already reported titles survives a bounded streak of failed
/// polls, and every way the loop ends says whether titles were reported. Each scenario runs against a
/// real loopback server scripted per connection, one connection per poll.
/// </summary>
public sealed class IcecastStatusReaderTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task AFailedPollBetweenSuccessfulOnesIsRiddenOut()
    {
        using var server = ScriptedServer.Start();
        server.Script(server.Status("One"), server.Unavailable(), server.Status("Two"), server.Unavailable());
        using var client = NewClient();
        var recorder = new TitleRecorder();
        using var cts = new CancellationTokenSource(TestDeadline);

        var read = NewReader(client).ReadAsync(new Uri(server.Url), recorder, cts.Token);
        await recorder.Second.WaitAsync(TestDeadline); // the poll after the 503 succeeded
        cts.Cancel();

        var result = await read;
        Assert.Equal(IcecastStatusReadOutcome.Cancelled, result.Outcome); // never EndpointUnavailable
        Assert.True(result.TitlesReported);
        Assert.Equal(["One", "Two"], recorder.Titles);
    }

    /// <summary>SP-0172 also tolerates an unreadable document between good ones, not only refusals.</summary>
    [Fact]
    public async Task AnUnreadableDocumentBetweenTitlesIsTolerated()
    {
        using var server = ScriptedServer.Start();
        server.Script(server.Status("One"), server.Malformed(), server.Status("Two"), server.Unavailable());
        using var client = NewClient();
        var recorder = new TitleRecorder();
        using var cts = new CancellationTokenSource(TestDeadline);

        var read = NewReader(client).ReadAsync(new Uri(server.Url), recorder, cts.Token);
        await recorder.Second.WaitAsync(TestDeadline);
        cts.Cancel();

        var result = await read;
        Assert.Equal(IcecastStatusReadOutcome.Cancelled, result.Outcome);
        Assert.True(result.TitlesReported);
        Assert.Equal(["One", "Two"], recorder.Titles);
    }

    [Fact]
    public async Task GivingUpAfterTitlesSaysSo()
    {
        using var server = ScriptedServer.Start();
        server.Script(
            [server.Status("One"), .. Enumerable.Repeat(server.Unavailable(), IcecastStatusReader.MaximumConsecutiveFailures)]);
        using var client = NewClient();
        var recorder = new TitleRecorder();
        using var cts = new CancellationTokenSource(TestDeadline);

        var result = await NewReader(client).ReadAsync(new Uri(server.Url), recorder, cts.Token);

        Assert.Equal(IcecastStatusReadOutcome.EndpointUnavailable, result.Outcome);
        Assert.True(result.TitlesReported);
        Assert.Equal(["One"], recorder.Titles);
        Assert.Equal(1 + IcecastStatusReader.MaximumConsecutiveFailures, server.Connections);
    }

    /// <summary>The tolerance belongs to an endpoint that worked; one that never has falls back at once.</summary>
    [Fact]
    public async Task AFirstFailureBeforeAnyTitleEndsTheReadImmediately()
    {
        using var server = ScriptedServer.Start();
        server.Script(server.Unavailable());
        using var client = NewClient();

        var result = await NewReader(client).ReadAsync(new Uri(server.Url), new TitleRecorder(), TestCts());

        Assert.Equal(IcecastStatusReadOutcome.EndpointUnavailable, result.Outcome);
        Assert.False(result.TitlesReported);
        Assert.Equal(1, server.Connections);
    }

    /// <summary>
    /// SP-0172: a readable document describing other mounts is a working endpoint that cannot serve
    /// this channel - not the same event as an answer that is no status document at all.
    /// </summary>
    [Fact]
    public async Task OtherMountsOnlyMapsToNoMatchingMount()
    {
        using var server = ScriptedServer.Start();
        server.Script(server.OtherMountOnly());
        using var client = NewClient();

        var result = await NewReader(client).ReadAsync(new Uri(server.Url), new TitleRecorder(), TestCts());

        Assert.Equal(IcecastStatusReadOutcome.NoMatchingMount, result.Outcome);
        Assert.False(result.TitlesReported);
    }

    [Fact]
    public async Task AnAnswerThatIsNoStatusDocumentMapsToMalformed()
    {
        using var server = ScriptedServer.Start();
        server.Script(server.Malformed());
        using var client = NewClient();

        var result = await NewReader(client).ReadAsync(new Uri(server.Url), new TitleRecorder(), TestCts());

        Assert.Equal(IcecastStatusReadOutcome.Malformed, result.Outcome);
        Assert.False(result.TitlesReported);
    }

    private static HttpClient NewClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    private static IcecastStatusReader NewReader(HttpClient client) =>
        new(client, RequestTimeout, FastPoll);

    private static CancellationToken TestCts() => new CancellationTokenSource(TestDeadline).Token;

    /// <summary>Answers each connection with the next queued script; the last one repeats.</summary>
    private sealed class ScriptedServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Queue<Func<NetworkStream, CancellationToken, Task>> _scripts = new();
        private int _connections;

        private ScriptedServer(TcpListener listener)
        {
            _listener = listener;
            _ = Task.Run(AcceptLoopAsync);
        }

        internal string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/live";

        internal int Connections => Volatile.Read(ref _connections);

        internal static ScriptedServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new ScriptedServer(listener);
        }

        internal void Script(params Func<NetworkStream, CancellationToken, Task>[] scripts)
        {
            foreach (var script in scripts)
            {
                lock (_scripts)
                {
                    _scripts.Enqueue(script);
                }
            }
        }

        internal Func<NetworkStream, CancellationToken, Task> Status(string title) =>
            Answer(StatusJson(title));

        internal Func<NetworkStream, CancellationToken, Task> OtherMountOnly() =>
            Answer($$"""{ "icestats": { "source": { "listenurl": "{{Url}}other", "title": "Someone else" } } }""");

        internal Func<NetworkStream, CancellationToken, Task> Unavailable() =>
            (stream, token) => HoldAsync(stream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);

        internal Func<NetworkStream, CancellationToken, Task> Malformed() =>
            Answer("not json");

        private Func<NetworkStream, CancellationToken, Task> Answer(string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {bytes.Length}\r\n" +
                "Connection: close\r\n\r\n";
            return (stream, token) => HoldAsync(stream, head + body, token);
        }

        private string StatusJson(string title) =>
            $$"""{ "icestats": { "source": { "listenurl": "{{Url}}", "title": "{{title}}" } } }""";

        private static async Task HoldAsync(NetworkStream stream, string response, CancellationToken token)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
            // The socket stays open until the test disposes the server; the reader needs nothing more
            // from a response it has fully read, and a held socket cannot serve a second poll by accident.
            await Task.Delay(Timeout.Infinite, token);
        }

        private async Task AcceptLoopAsync()
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
                            _ = await stream.ReadAsync(new byte[4096], _stop.Token); // the request head
                            Func<NetworkStream, CancellationToken, Task>? script;
                            lock (_scripts)
                            {
                                script = _scripts.Count > 0 ? _scripts.Dequeue() : null;
                            }

                            if (script is not null)
                            {
                                await script(stream, _stop.Token);
                            }
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

    private sealed class TitleRecorder : IProgress<string?>
    {
        private readonly object _sync = new();
        private readonly List<string?> _titles = [];
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task First => _first.Task;

        internal Task Second => _second.Task;

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
                if (_titles.Count == 1)
                {
                    _first.TrySetResult();
                }

                if (_titles.Count == 2)
                {
                    _second.TrySetResult();
                }
            }
        }
    }
}
