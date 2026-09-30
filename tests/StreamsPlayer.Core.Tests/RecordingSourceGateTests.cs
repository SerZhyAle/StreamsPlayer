namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0164: the recording seams a WPF-bound test cannot reach, held over the App's own sources. The
/// behaviour itself is proven by <see cref="RecordingWriteProbeTests"/>, <see cref="RecordingFinisherTests"/>
/// and <see cref="StreamAudioRecorderTests"/>; these gates lock the wiring that joins it.
/// </summary>
public sealed class RecordingSourceGateTests
{
    [Fact]
    public void TheLibVlcEngineReportsItsOwnRecordingState()
    {
        var source = Source("LibVlcVideoBackend.cs");
        Assert.Contains("_recording.IsWriting(", source);
    }

    [Fact]
    public void ThePlayerTickConsultsTheEngineState()
    {
        // ObserveRecording runs on the stats tick; this read is what turns an engine that stopped writing
        // into a collected segment within one tick.
        var source = Source("PlayerWindow.Recording.cs");
        var observe = source[source.IndexOf("private void ObserveRecording()", StringComparison.Ordinal)..];
        Assert.Contains("_backend.IsRecording", observe);
    }

    [Fact]
    public void TheCloseWorkWaitsForTheRecordingFinishes()
    {
        var source = Source("MainWindow.Previews.cs");
        Assert.Contains("await Task.WhenAll(_closedRecordingFinishes)", source);
    }

    [Fact]
    public void TheMoveGoesThroughAPartialRename()
    {
        var source = Source("RecordingFinisher.cs");
        Assert.Contains("File.Copy(file, partial, overwrite: true)", source);
        Assert.Contains("File.Move(partial, destination, overwrite: false)", source);
    }

    private static string Source(string name)
    {
        var path = Path.Combine(AppSourceFile.Directory, name);
        Assert.True(File.Exists(path), $"{name} is not among the copied App sources.");
        return File.ReadAllText(path);
    }
}
