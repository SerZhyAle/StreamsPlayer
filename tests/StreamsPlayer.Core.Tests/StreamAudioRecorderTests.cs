using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0164: the radio recorder's own contract. An end is heard even when it is raised before the
/// owner subscribed (S47-08); a recording that wrote nothing leaves no file (S46-12); a folder that
/// refuses the file is a write failure, not a connection failure (S46-13, locked against the SP-0179
/// rework already in the tree). The recorder's sources are compiled into this project as WPF-free
/// sources; the one-shot server speaks raw HTTP so no playback stack is needed.
/// </summary>
public sealed class StreamAudioRecorderTests
{
    [Fact]
    public async Task AnImmediateEndIsRaisedToTheOwnerEvenThoughItSubscribedAfterStart()
    {
        using var server = OneShotServer.Start("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
        var folder = NewFolder();
        using var log = new CurrentLog(NewFolder());
        var recorder = StreamAudioRecorder.Start(Channel(server.Url), [folder], log, null);

        AudioRecordingOutcome? received = null;
        recorder.Ended += (_, outcome) => received = outcome; // the worker may already have ended here

        await WaitUntil(() => received is not null, TimeSpan.FromSeconds(10));
        Assert.Equal(AudioRecordingEnd.NotConnected, received!.End);
        Assert.Null(received.Path);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task ARecordingThatWroteNothingLeavesNoFile()
    {
        using var server = OneShotServer.Start("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nConnection: close\r\n\r\n");
        var folder = NewFolder();
        using var log = new CurrentLog(NewFolder());
        var recorder = StreamAudioRecorder.Start(Channel(server.Url), [folder], log, null);

        AudioRecordingOutcome? received = null;
        recorder.Ended += (_, outcome) => received = outcome;

        await WaitUntil(() => received is not null, TimeSpan.FromSeconds(10));
        Assert.Equal(AudioRecordingEnd.ConnectionLost, received!.End); // the station sent nothing and closed
        Assert.Null(received.Path);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task ARefusedFolderIsAWriteFailureNotAConnectionFailure()
    {
        using var server = OneShotServer.Start("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nConnection: close\r\n\r\nID3");
        var occupied = Path.Combine(NewFolder(), "occupied");
        File.WriteAllText(occupied, "a file where the recording folder would go");
        using var log = new CurrentLog(NewFolder());
        var recorder = StreamAudioRecorder.Start(Channel(server.Url), [occupied], log, null);

        AudioRecordingOutcome? received = null;
        recorder.Ended += (_, outcome) => received = outcome;

        await WaitUntil(() => received is not null, TimeSpan.FromSeconds(10));
        Assert.Equal(AudioRecordingEnd.WriteFailed, received!.End);
        Assert.Null(received.Path);
    }

    private static StreamChannel Channel(Uri url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url.ToString(),
        Title = "SP-0164 test station",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Manual,
        AddedAt = DateTimeOffset.Now
    };

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sp0164-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "the recording never raised its end");
    }

    /// <summary>Serves one raw response to the first connection and closes; the recorder never needs its request read.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener;

        private OneShotServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        public Uri Url { get; private set; } = null!;

        public static OneShotServer Start(string response)
        {
            var server = new OneShotServer();
            server.Url = new Uri($"http://127.0.0.1:{((IPEndPoint)server._listener.LocalEndpoint).Port}/live.aac");
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await server._listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                    client.Close();
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
                {
                    // Stopped mid-serve or the client went away; the test has what it came for.
                }
            });
            return server;
        }

        /// <summary>
        /// Reads to the end of the request headers before answering. Answering unread leaves the request
        /// in flight and the client's next read takes a reset, which the recorder would report as a
        /// connection failure instead of the verdict under test.
        /// </summary>
        private static async Task ReadRequestHeadersAsync(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var seen = 0;
            while (seen < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(seen));
                if (read == 0 || Encoding.ASCII.GetString(buffer, 0, seen += read).Contains("\r\n\r\n"))
                {
                    return;
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
