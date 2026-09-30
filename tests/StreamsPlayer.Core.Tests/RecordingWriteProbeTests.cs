using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0164: the engine-state double for the recording indicator (S43-02). A start the engine accepted and
/// later refused keeps no file of its own in staging; the probe is what turns that into "not recording" at
/// the player's first tick past the grace window. Real temp directories stand in for the engine's staging.
/// </summary>
public sealed class RecordingWriteProbeTests : IDisposable
{
    private readonly string _leg = NewFolder();
    private readonly string _instance = NewFolder();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_leg, recursive: true);
            Directory.Delete(_instance, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private IReadOnlyList<string> Staging => [_leg, _instance];

    [Fact]
    public void AYoungStartCountsAsWritingEvenWithNothingToShow()
    {
        Assert.True(RecordingWriteProbe.IsWriting(_startedAt, Staging, new HashSet<string>(), _startedAt + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void APastGraceStartWithAFileOfItsOwnCountsAsWriting()
    {
        File.WriteAllText(Path.Combine(_leg, "vlc-record-2026-09-29-12h00m00s.ext"), "video");
        Assert.True(RecordingWriteProbe.IsWriting(_startedAt, Staging, new HashSet<string>(), _startedAt + RecordingWriteProbe.AppearGrace + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void APastGraceStartWithOnlyPreexistingFilesCountsAsNotWriting()
    {
        var preexisting = Path.Combine(_leg, "someone-elses.mp4");
        File.WriteAllText(preexisting, "was here before the segment started");
        Assert.False(RecordingWriteProbe.IsWriting(_startedAt, Staging, new HashSet<string> { preexisting }, _startedAt + RecordingWriteProbe.AppearGrace + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void APastGraceStartWithAnEmptyStagingCountsAsNotWriting()
    {
        // The refused recording: the request was accepted, the grace is gone, no file ever appeared.
        Assert.False(RecordingWriteProbe.IsWriting(_startedAt, Staging, new HashSet<string>(), _startedAt + RecordingWriteProbe.AppearGrace + TimeSpan.FromSeconds(1)));
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sp0164-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
