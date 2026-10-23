using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastAudioReadStreamTests
{
    [Fact]
    public async Task ARefusedRecordingFileDoesNotCloseOrChangeTheListeningResponse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sp0236-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var log = new CurrentLog(Path.Combine(directory, "log"));
        try
        {
            var refused = Path.Combine(directory, "not-a-folder");
            File.WriteAllText(refused, "occupied");
            var source = AudioRecordingFramerTests.Wave(9000, 2);
            using var tap = new BroadcastAudioReadStream(new MemoryStream(source), "audio/wav", refused);
            var decoded = new byte[source.Length];
            await tap.ReadExactlyAsync(decoded.AsMemory(0, 4096));
            Assert.True(tap.StartRecording(new RecordingTarget(directory, null, [], CaptureKind.StreamAudio), log));
            var recorder = Assert.IsType<BroadcastAudioRecorder>(tap.EndRecording());
            await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(recorder.Failed);
            await tap.ReadExactlyAsync(decoded.AsMemory(4096));
            Assert.Equal(source, decoded);
        }
        finally
        {
            log.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheDecoderReadsTheOnlyResponseWhileALateRecordingGetsItsOwnWaveHeader()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sp0236-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var log = new CurrentLog(Path.Combine(directory, "log"));
        try
        {
            var source = AudioRecordingFramerTests.Wave(20000, 4);
            using var response = new MemoryStream(source);
            using var tap = new BroadcastAudioReadStream(response, "audio/wav", Path.Combine(directory, "staging"));
            var decoded = new byte[source.Length];
            Assert.Equal(4097, tap.Read(decoded, 0, 4097));
            Assert.True(tap.CanRecord);
            Assert.True(tap.StartRecording(new RecordingTarget(directory, "Broadcast", [], CaptureKind.StreamAudio), log));
            var read = await tap.ReadAsync(decoded.AsMemory(4097, 8001));
            Assert.Equal(8001, read);
            var recorder = Assert.IsType<BroadcastAudioRecorder>(tap.EndRecording());
            var segment = await recorder.Completion;
            Assert.False(recorder.Failed);
            var saved = Assert.Single(await RecordingFinisher.FinishAsync(segment, log));
            Assert.Equal(SegmentFate.Saved, saved.Fate);
            var recorded = File.ReadAllBytes(saved.Path!);
            Assert.Equal(source.AsSpan(4100, 7996).ToArray(), recorded.AsSpan(44).ToArray());
            Assert.Equal(12098, response.Position);
            await tap.ReadExactlyAsync(decoded.AsMemory(12098));
            Assert.Equal(source, decoded);
        }
        finally
        {
            // Dispose the log before cleanup on Windows: its handle deliberately remains open for the session.
            log.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EndOfResponseClosesTheRecorderAndAnEmptyReadDoesNotEndTheListeningLeg()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sp0236-eof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var log = new CurrentLog(Path.Combine(directory, "log"));
        try
        {
            using var tap = new BroadcastAudioReadStream(new MemoryStream(AudioRecordingFramerTests.Wave(9000, 2)),
                "audio/wav", Path.Combine(directory, "staging"));
            var buffer = new byte[4096];
            await tap.ReadExactlyAsync(buffer);
            Assert.Equal(0, tap.Read([]));
            Assert.True(tap.CanRecord);
            Assert.True(tap.StartRecording(new RecordingTarget(directory, null, [], CaptureKind.StreamAudio), log));
            while (await tap.ReadAsync(buffer) != 0) { }
            Assert.False(tap.CanRecord);
            var recorder = Assert.IsType<BroadcastAudioRecorder>(tap.EndRecording());
            await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(recorder.Failed);
        }
        finally
        {
            log.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
