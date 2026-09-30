using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>How a radio recording ended.</summary>
internal enum AudioRecordingEnd
{
    /// <summary>The user stopped it (or playback stopped and took it along).</summary>
    Stopped,

    /// <summary>The station closed the connection, or it went silent past the idle limit.</summary>
    ConnectionLost,

    /// <summary>The station never delivered audio - an HTTP error or no connection at all.</summary>
    NotConnected,

    /// <summary>What the station sends is not something a byte copy can record (HLS, or unidentifiable).</summary>
    UnsupportedFormat,

    /// <summary>The file could not be written.</summary>
    WriteFailed
}

/// <summary>What a radio recording produced. <see cref="Path"/> is null when nothing was written.</summary>
internal sealed record AudioRecordingOutcome(AudioRecordingEnd End, string? Path, long Bytes, TimeSpan Length, string? Detail = null);

/// <summary>
/// SP-0101 / SP-0121: records a radio station by copying its stream to a file. The copy runs on its own connection
/// because the audio engine exposes no tap on its own - which is also why a FastMediaSorter broadcast, whose route
/// promises one listener connection, is never recorded here (the caller refuses it, R6).
/// <para>SP-0121 changed four things. The file is named only once the stream has been seen, from what it actually
/// is (C-09); a station given as a playlist link records the stream the list points at. A recording that ends on
/// its own - the station dropped it - raises <see cref="Ended"/> with the part that was written, instead of going
/// quiet while the window kept saying "recording" (C-07). <see cref="StopAsync"/> never blocks the UI thread. And a
/// read that stays silent past <see cref="IdleLimit"/> counts as a dropped connection rather than a recording that
/// silently stops growing.</para>
/// </summary>
internal sealed class StreamAudioRecorder
{
    /// <summary>A station that sends nothing for this long has dropped the recording.</summary>
    internal static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Client = CreateClient();

    private readonly CancellationTokenSource _stop = new();
    /// <summary>Recordings racing for one name in one second; a longer run means something else is wrong.</summary>
    private const int MaxCreateAttempts = 5;

    private readonly CurrentLog _log;
    private readonly IReadOnlyList<string> _chain;
    private readonly Action<string, string>? _onRedirected;
    private readonly string? _title;
    private readonly Stopwatch _written = new();
    private readonly Task<AudioRecordingOutcome> _worker;
    private readonly object _endedGate = new();
    private Action<StreamAudioRecorder, AudioRecordingOutcome>? _endedHandlers;
    private AudioRecordingOutcome? _endedOutcome;
    private bool _endedDelivered;
    private int _stopRequested;
    private long _bytes;

    private StreamAudioRecorder(StreamChannel channel, IReadOnlyList<string> chain, CurrentLog log, Action<string, string>? onRedirected)
    {
        _log = log;
        _chain = chain;
        _onRedirected = onRedirected;
        _title = StreamTitleFormatter.Display(channel.Title);
        StartedAt = DateTimeOffset.Now;
        _worker = Task.Run(() => RecordAsync(channel.Url));
    }

    internal DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Raised once when the recording ends for any reason but <see cref="StopAsync"/>. Not raised after a stop
    /// was asked for - the caller of <see cref="StopAsync"/> gets the outcome from it. Raised on the worker
    /// thread - unless the recording ended before the owner's subscription made it back from <see cref="Start"/>,
    /// in which case the stored outcome is delivered on the subscribing thread (SP-0164: a recording that ends at
    /// once can no longer end unheard).
    /// </summary>
    internal event Action<StreamAudioRecorder, AudioRecordingOutcome>? Ended
    {
        add
        {
            lock (_endedGate)
            {
                _endedHandlers += value;
                DeliverEndedUnderGate();
            }
        }
        remove
        {
            lock (_endedGate)
            {
                _endedHandlers -= value;
            }
        }
    }

    /// <summary>
    /// Starts recording into the first folder of <paramref name="chain"/> that takes the file (SP-0179, CAPTURE-OUTPUT
    /// rule 11). <paramref name="onRedirected"/> is called on the worker with the refused first folder and the one
    /// used, when they differ; it is passed in rather than subscribed because the worker may reach the file before
    /// a subscription made after this returns.
    /// </summary>
    internal static StreamAudioRecorder Start(StreamChannel channel, IReadOnlyList<string> chain, CurrentLog log, Action<string, string>? onRedirected)
    {
        log.Event("AUDIO RECORD START", $"channel={channel.Title}", $"folder={chain[0]}", $"fallbacks={chain.Count - 1}");
        return new StreamAudioRecorder(channel, chain, log, onRedirected);
    }

    /// <summary>Stops the copy and returns what it produced. Awaitable from the UI thread; the wait is on the worker.</summary>
    internal async Task<AudioRecordingOutcome> StopAsync()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        await _stop.CancelAsync().ConfigureAwait(false);
        return await _worker.ConfigureAwait(false);
    }

    private bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    private async Task<AudioRecordingOutcome> RecordAsync(string url)
    {
        var outcome = await CopyAsync(url).ConfigureAwait(false);
        _log.Event("AUDIO RECORD END",
            $"end={outcome.End}",
            $"bytes={outcome.Bytes}",
            $"length={RecordingLength.Format(outcome.Length)}",
            $"path={outcome.Path ?? "none"}",
            $"detail={outcome.Detail ?? "none"}");
        RaiseEnded(outcome);
        return outcome;
    }

    private void RaiseEnded(AudioRecordingOutcome outcome)
    {
        lock (_endedGate)
        {
            if (StopRequested)
            {
                return;
            }

            _endedOutcome = outcome;
            DeliverEndedUnderGate();
        }
    }

    private void DeliverEndedUnderGate()
    {
        if (_endedDelivered || _endedOutcome is not { } outcome || _endedHandlers is not { } handlers)
        {
            return;
        }

        _endedDelivered = true;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<StreamAudioRecorder, AudioRecordingOutcome>>())
        {
            try
            {
                handler(this, outcome);
            }
            catch (Exception exception)
            {
                _log.Event("AUDIO RECORD END", "handed_over=false", $"err={exception.Message}");
            }
        }
    }

    private async Task<AudioRecordingOutcome> CopyAsync(string url)
    {
        var token = _stop.Token;
        string? path = null;
        try
        {
            // SP-0124: a recording follows the station, so the station's address rule is the recording's.
            if (!LaunchableAddress.TryParseHttp(url, out var address))
            {
                return Outcome(AudioRecordingEnd.NotConnected, null, "unsupported_address");
            }

            for (var hop = 0; ; hop++)
            {
                using var response = await Client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return Outcome(AudioRecordingEnd.NotConnected, null, $"http={(int)response.StatusCode}");
                }

                await using var network = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var head = await ReadHeadAsync(network, token).ConfigureAwait(false);
                var kind = RecordedAudioFormat.Classify(response.Content.Headers.ContentType?.ToString(), address.ToString(), head);
                switch (kind.Body)
                {
                    case RecordedAudioBody.Playlist:
                        if (hop + 1 >= StationPlaylist.MaximumHops
                            || StationPlaylist.FirstStream(await ReadPlaylistAsync(head, network, token).ConfigureAwait(false), address) is not { } next)
                        {
                            return Outcome(AudioRecordingEnd.NotConnected, null, "playlist_unresolved");
                        }

                        _log.Event("AUDIO RECORD PLAYLIST", $"from={address}", $"to={next}");
                        address = next;
                        continue;
                    case RecordedAudioBody.HlsManifest:
                        return Outcome(AudioRecordingEnd.UnsupportedFormat, null, "hls");
                    case RecordedAudioBody.Unknown:
                        return Outcome(AudioRecordingEnd.UnsupportedFormat, null, $"content_type={response.Content.Headers.ContentType?.MediaType ?? "none"}");
                }

                FileStream file;
                string? skipped;
                try
                {
                    (file, path, skipped) = CreateInChain(CaptureFileName.For(CaptureKind.StreamAudio, StartedAt, _title, kind.Extension));
                }
                catch (Exception exception) when (CaptureFolders.IsFolderRefusal(exception))
                {
                    return Failed(AudioRecordingEnd.WriteFailed, null, exception);
                }

                _log.Event("AUDIO RECORD FILE", $"format={kind.Extension}", $"content_type={response.Content.Headers.ContentType?.MediaType ?? "none"}", $"path={path}", $"skipped={skipped ?? "none"}");
                if (skipped is not null)
                {
                    _onRedirected?.Invoke(skipped, Path.GetDirectoryName(path) ?? path);
                }

                var outcome = await WriteAsync(network, head, file, path, token).ConfigureAwait(false);
                if (outcome.Bytes == 0 && path is not null)
                {
                    // SP-0164: a recording that wrote nothing leaves no file - the video path deletes such
                    // files too. Here, past the await using, the stream is closed and the delete can
                    // succeed; Outcome has already dropped the path from the outcome itself.
                    TryDeleteEmpty(path);
                }

                return outcome;
            }
        }
        catch (OperationCanceledException) when (StopRequested)
        {
            return Outcome(AudioRecordingEnd.Stopped, path, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            // Before the file exists this is a failed start; the write loop reports its own drops.
            return Failed(AudioRecordingEnd.NotConnected, null, exception);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failed(AudioRecordingEnd.WriteFailed, path, exception);
        }
    }

    /// <summary>
    /// SP-0179: creates the recording's file in the first folder of the chain that takes it, choosing the name
    /// there before the write (CAPTURE-OUTPUT rules 5-6) and taking the next ordinal when another recording wins
    /// the same name first. Returns the refused first folder as well when the file landed further down the chain.
    /// </summary>
    private (FileStream File, string Path, string? Skipped) CreateInChain(string fileName)
    {
        ExceptionDispatchInfo? last = null;
        for (var index = 0; index < _chain.Count; index++)
        {
            try
            {
                var (file, path) = CreateIn(_chain[index], fileName);
                return (file, path, index == 0 ? null : _chain[0]);
            }
            catch (Exception exception) when (CaptureFolders.IsFolderRefusal(exception))
            {
                _log.Event("AUDIO RECORD FOLDER", "ok=false", $"folder={_chain[index]}", $"err={exception.Message}");
                last = ExceptionDispatchInfo.Capture(exception);
            }
        }

        last?.Throw();
        throw new IOException("No folder to record into.");
    }

    private static (FileStream File, string Path) CreateIn(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        for (var attempt = 1; ; attempt++)
        {
            var path = CaptureFolders.ReserveUniquePath(folder, fileName);
            try
            {
                return (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024, useAsync: true), path);
            }
            catch (IOException) when (attempt < MaxCreateAttempts && File.Exists(path))
            {
                // Lost the name to another recording in the same second; reserve the next one.
            }
        }
    }

    private async Task<AudioRecordingOutcome> WriteAsync(Stream network, byte[] head, FileStream file, string path, CancellationToken token)
    {
        await using (file.ConfigureAwait(false))
        {
            _written.Start();
            if (!await TryWriteAsync(file, head).ConfigureAwait(false))
            {
                return Outcome(AudioRecordingEnd.WriteFailed, path, "write");
            }

            var buffer = new byte[32 * 1024];
            while (true)
            {
                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(IdleLimit);
                    try
                    {
                        read = await network.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (StopRequested)
                    {
                        return Outcome(AudioRecordingEnd.Stopped, path, null);
                    }
                    catch (OperationCanceledException)
                    {
                        return Outcome(AudioRecordingEnd.ConnectionLost, path, "idle");
                    }
                    catch (Exception exception) when (exception is IOException or HttpRequestException)
                    {
                        return StopRequested
                            ? Outcome(AudioRecordingEnd.Stopped, path, null)
                            : Failed(AudioRecordingEnd.ConnectionLost, path, exception);
                    }
                }

                if (read <= 0)
                {
                    return Outcome(StopRequested ? AudioRecordingEnd.Stopped : AudioRecordingEnd.ConnectionLost, path, "eof");
                }

                if (!await TryWriteAsync(file, buffer.AsMemory(0, read)).ConfigureAwait(false))
                {
                    return Outcome(AudioRecordingEnd.WriteFailed, path, "write");
                }
            }
        }
    }

    /// <summary>Writes without the stop token: a stop must never leave half a buffer in the file.</summary>
    private async Task<bool> TryWriteAsync(FileStream file, ReadOnlyMemory<byte> data)
    {
        try
        {
            await file.WriteAsync(data).ConfigureAwait(false);
            Interlocked.Add(ref _bytes, data.Length);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Event("AUDIO RECORD ERROR", $"err={exception.Message}", $"path={file.Name}");
            return false;
        }
    }

    /// <summary>An outcome caused by an exception: its text goes to the log, only its type into the outcome.</summary>
    private AudioRecordingOutcome Failed(AudioRecordingEnd end, string? path, Exception exception)
    {
        _log.Event("AUDIO RECORD ERROR", $"end={end}", $"err={exception.Message}");
        return Outcome(end, path, exception.GetType().Name);
    }

    private void TryDeleteEmpty(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Event("AUDIO RECORD ERROR", "reason=empty_file_kept", $"path={path}", $"err={exception.Message}");
        }
    }

    private AudioRecordingOutcome Outcome(AudioRecordingEnd end, string? path, string? detail)
    {
        _written.Stop();
        var bytes = Interlocked.Read(ref _bytes);
        return new AudioRecordingOutcome(end, bytes > 0 ? path : null, bytes, _written.Elapsed, detail);
    }

    private static async Task<byte[]> ReadHeadAsync(Stream network, CancellationToken token)
    {
        var head = new byte[RecordedAudioFormat.SniffLength];
        var filled = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(IdleLimit);
        while (filled < head.Length)
        {
            var read = await network.ReadAsync(head.AsMemory(filled), idle.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            filled += read;
        }

        return head[..filled];
    }

    private static async Task<string> ReadPlaylistAsync(byte[] head, Stream network, CancellationToken token)
    {
        using var body = new MemoryStream();
        body.Write(head);
        var buffer = new byte[4096];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(IdleLimit);
        while (body.Length < StationPlaylist.MaximumBodyBytes)
        {
            var read = await network.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            body.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)Math.Min(body.Length, StationPlaylist.MaximumBodyBytes));
    }

    private static HttpClient CreateClient()
    {
        // One client for every recording: connections are pooled rather than a socket per press. The timeout
        // covers reaching the headers only; the body is bounded by IdleLimit per read instead.
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }
}
