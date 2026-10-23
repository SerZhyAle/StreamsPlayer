using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>The decoder remains the only reader of the only response. Recording never advances that reader.</summary>
internal sealed class BroadcastAudioReadStream(Stream inner, string? contentType, string? stagingDirectory = null) : Stream
{
    private readonly object _gate = new();
    private readonly AudioRecordingFramer _framer = new(contentType);
    private readonly string _directory = stagingDirectory ?? RecordingStaging.NewInstanceDirectory(CaptureKind.StreamAudio);
    private BroadcastAudioRecorder? _recorder;
    private bool _ended;

    internal bool CanRecord { get { lock (_gate) return !_ended && _framer.Ready; } }
    internal bool IsRecordingWriting { get { lock (_gate) return _recorder is { Completion.IsCompleted: false }; } }

    internal bool StartRecording(RecordingTarget target, CurrentLog log)
    {
        lock (_gate)
        {
            if (!CanRecord || _recorder is not null) return false;
            // The worker owns its own snapshot: another recording may begin before this one finishes moving.
            var header = _framer.Begin();
            var finalHeader = _framer.FinalHeader;
            var align = _framer.AlignedDataLength;
            _recorder = new BroadcastAudioRecorder(target, Path.Combine(_directory, Guid.NewGuid().ToString("N")),
                _framer.Extension!, header, finalHeader, align, log);
            return true;
        }
    }

    internal BroadcastAudioRecorder? EndRecording()
    {
        lock (_gate)
        {
            var recorder = _recorder;
            _recorder = null;
            _framer.End();
            recorder?.Complete();
            return recorder;
        }
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            var read = inner.Read(buffer[..Math.Min(buffer.Length, 64 * 1024)]);
            Observe(buffer[..read], end: read == 0 && buffer.Length > 0);
            return read;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Observe([], end: true);
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], cancellationToken).ConfigureAwait(false);
            Observe(buffer.Span[..read], end: read == 0 && buffer.Length > 0);
            return read;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Observe([], end: true);
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Observe(ReadOnlySpan<byte> bytes, bool end)
    {
        lock (_gate)
        {
            if (_ended) return;
            foreach (var block in _framer.Push(bytes)) _recorder?.Offer(block);
            if (end || (_recorder is not null && !_framer.Ready))
            {
                _ended = end;
                _recorder?.Complete(failed: !end);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Observe([], end: true);
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
