using System.IO;
using System.Net.Http;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0101: records a live audio stream (radio/podcast/FMS broadcast) directly to an audio file on disk.
/// Streams chunks in the background without affecting UI responsiveness or playback.
/// </summary>
internal sealed class StreamAudioRecorder : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _recordTask;
    private readonly CurrentLog _log;
    private readonly string _destinationPath;
    private readonly DateTimeOffset _startTime;
    private volatile bool _isRecording;
    private long _bytesWritten;
    private bool _disposed;

    private StreamAudioRecorder(StreamChannel channel, string destinationPath, CurrentLog log)
    {
        _log = log;
        _destinationPath = destinationPath;
        _startTime = DateTimeOffset.Now;
        _isRecording = true;
        _recordTask = Task.Run(() => RecordLoopAsync(channel.Url, _destinationPath, _cts.Token));
    }

    public bool IsRecording => _isRecording;
    public string DestinationPath => _destinationPath;
    public DateTimeOffset StartTime => _startTime;
    public TimeSpan Elapsed => DateTimeOffset.Now - _startTime;
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public static StreamAudioRecorder? Start(StreamChannel channel, string? targetFolder, CurrentLog log)
    {
        try
        {
            var folder = RecordedBroadcastWriter.ResolveFolder(targetFolder);
            Directory.CreateDirectory(folder);

            var fileName = RecordedBroadcastName.For(channel.Title, DateTimeOffset.Now, RecordedBroadcastName.DefaultAudioExtension);
            var destinationPath = RecordedBroadcastWriter.ReserveUniquePath(folder, fileName);

            log.Event("AUDIO RECORD START", $"channel={channel.Title}", $"path={destinationPath}");
            return new StreamAudioRecorder(channel, destinationPath, log);
        }
        catch (Exception ex)
        {
            log.Event("AUDIO RECORD START", "ok=false", $"err={ex.Message}");
            return null;
        }
    }

    public string? Stop()
    {
        if (!_isRecording)
        {
            return null;
        }

        _isRecording = false;
        try
        {
            _cts.Cancel();
            _recordTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Worker shutdown wait timed out or threw; file handle is closed upon loop exit
        }

        _log.Event("AUDIO RECORD STOP", $"path={_destinationPath}", $"bytes={BytesWritten}");
        return File.Exists(_destinationPath) && BytesWritten > 0 ? _destinationPath : null;
    }

    private async Task RecordLoopAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.Event("AUDIO RECORD HTTP", $"status={(int)response.StatusCode}", $"url={url}");
                return;
            }

            await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

            var buffer = new byte[32 * 1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                Interlocked.Add(ref _bytesWritten, read);
            }

            await fileStream.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on clean stop
        }
        catch (Exception ex)
        {
            _log.Event("AUDIO RECORD ERROR", $"err={ex.Message}", $"path={destinationPath}");
        }
        finally
        {
            _isRecording = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}
