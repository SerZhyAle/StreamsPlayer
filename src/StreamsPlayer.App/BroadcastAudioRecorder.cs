using System.IO;
using System.Threading.Channels;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>A bounded disk consumer of the listening leg's encoded bytes; it has no network capability.</summary>
internal sealed class BroadcastAudioRecorder
{
    // A slow/full disk ends recording instead of blocking sound. Count bytes as well as frames: a FLAC frame
    // may be larger than a network read, and a frame must be accepted or refused whole.
    private const int QueueBlocks = 128;
    private const int MaximumQueuedBytes = 8 * 1024 * 1024;
    private const int FileBufferBytes = 64 * 1024;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueBlocks)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CurrentLog _log;
    private int _failed;
    private long _endedUtcTicks;
    private long _queuedBytes;

    internal BroadcastAudioRecorder(RecordingTarget target, string directory, string extension, byte[] header,
        Func<long, byte[]> finalHeader, Func<long, long> alignedLength, CurrentLog log,
        Func<string, Stream>? openFile = null)
    {
        _log = log;
        StartedAt = DateTimeOffset.Now;
        Completion = Task.Run(() => WriteAsync(target, directory, extension, header, finalHeader, alignedLength, openFile));
    }

    internal DateTimeOffset StartedAt { get; }
    internal Task<RecordingSegment> Completion { get; }
    internal bool Failed => Volatile.Read(ref _failed) != 0;

    internal void Offer(byte[] data)
    {
        if (Interlocked.Add(ref _queuedBytes, data.Length) > MaximumQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -data.Length);
            Complete(failed: true);
        }
        else if (!_queue.Writer.TryWrite(data))
        {
            Interlocked.Add(ref _queuedBytes, -data.Length);
            Complete(failed: true);
        }
    }

    internal void Complete(bool failed = false)
    {
        Interlocked.CompareExchange(ref _endedUtcTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        if (failed) Interlocked.Exchange(ref _failed, 1);
        _queue.Writer.TryComplete();
    }

    private async Task<RecordingSegment> WriteAsync(RecordingTarget target, string directory, string extension,
        byte[] header, Func<long, byte[]> finalHeader, Func<long, long> alignedLength, Func<string, Stream>? openFile)
    {
        long bytes = 0;
        var path = Path.Combine(directory, "audio" + extension);
        try
        {
            Directory.CreateDirectory(directory);
            await using var file = openFile?.Invoke(path) ?? new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, FileBufferBytes, useAsync: true);
            try
            {
                await file.WriteAsync(header).ConfigureAwait(false);
                await foreach (var block in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    Interlocked.Add(ref _queuedBytes, -block.Length);
                    if (extension == RecordedAudioFormat.Wav && header.Length + bytes + block.Length - 8 > uint.MaxValue)
                    {
                        Complete(failed: true);
                        break;
                    }
                    await file.WriteAsync(block).ConfigureAwait(false);
                    bytes += block.Length;
                }
            }
            finally
            {
                // A failed write may have accepted half a frame. Keep only the completed writes and samples.
                file.SetLength(bytes == 0 ? 0 : header.Length + alignedLength(bytes));
                if (bytes > 0 && header.Length > 0)
                {
                    file.Position = 0;
                    await file.WriteAsync(finalHeader(bytes)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (CaptureFolders.IsFolderRefusal(exception))
        {
            Complete(failed: true);
            _log.Event("AUDIO RECORD ERROR", "route=broadcast_tap", $"err={exception.GetType().Name}");
        }

        return new RecordingSegment
        {
            Engine = "broadcast_tap", Target = target, StartedAt = StartedAt,
            EndedAt = new DateTimeOffset(Interlocked.Read(ref _endedUtcTicks), TimeSpan.Zero),
            StagingDirectories = [directory], ClosedByEngine = true
        };
    }
}
