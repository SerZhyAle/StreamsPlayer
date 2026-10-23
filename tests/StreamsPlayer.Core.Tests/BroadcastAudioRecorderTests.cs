using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastAudioRecorderTests
{
    [Fact]
    public async Task ABlockedDiskCannotBlockTheListenerOrAccumulateAnUnboundedQueue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sp0236-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var log = new CurrentLog(Path.Combine(directory, "log"));
        var file = new BlockedFile();
        try
        {
            var recorder = new BroadcastAudioRecorder(new RecordingTarget(directory, null, [], CaptureKind.StreamAudio),
                Path.Combine(directory, "staging"), ".flac", [], _ => [], length => length, log, _ => file);
            await file.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A large codec frame still must respect the queue's byte ceiling, rather than count as one block.
            var frame = new byte[1024 * 1024];
            await Task.Run(() =>
            {
                for (var count = 0; count < 9; count++) recorder.Offer(frame);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(recorder.Failed);
            Assert.False(recorder.Completion.IsCompleted);
            file.Release.TrySetResult();
            var segment = await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8 * 1024 * 1024, file.Written);
            Assert.True(segment.ClosedByEngine);
        }
        finally
        {
            file.Release.TrySetResult();
            log.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class BlockedFile : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long Written { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Written += buffer.Length;
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
