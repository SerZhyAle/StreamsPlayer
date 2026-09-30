using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0164: the move into the recordings folder cannot leave a truncated file under the recording's final
/// name. The bytes are copied into a <c>.recpartial</c> name and renamed, so the destination only ever
/// appears whole, and the staged original goes only after the rename.
/// </summary>
public sealed class RecordingFinisherTests : IDisposable
{
    private readonly string _staging = NewFolder();
    private readonly string _recordings = NewFolder();
    private readonly CurrentLog _log = new(NewFolder());

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_staging, recursive: true);
            Directory.Delete(_recordings, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void AFinishedSegmentArrivesWholeAndLeavesNoPartialOrStagedOriginal()
    {
        var staged = Path.Combine(_staging, "vlc-record-2026-09-29-12h00m00s.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(staged, payload);
        var segment = new RecordingSegment
        {
            Engine = "libvlc",
            Target = new RecordingTarget(_recordings, "SP-0164 channel", []),
            StartedAt = DateTimeOffset.Now.AddMinutes(-1),
            EndedAt = DateTimeOffset.Now,
            StagingDirectories = [_staging]
        };

        var saved = SegmentOf(RecordingFinisher.FinishAsync(segment, _log));

        Assert.Equal(SegmentFate.Saved, saved.Fate);
        Assert.Equal(payload, File.ReadAllBytes(saved.Path!));
        Assert.Empty(Directory.GetFiles(_recordings, $"*{RecordingFinisher.PartialSuffix}"));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void ASourceTheRecordingsFolderCannotLetGoOfLeavesTheSavedRecordingBehind()
    {
        var staged = Path.Combine(_staging, "vlc-record-2026-09-29-12h01m00s.mp4");
        var payload = new byte[4096];
        File.WriteAllBytes(staged, payload);
        using var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
        var segment = new RecordingSegment
        {
            Engine = "libvlc",
            Target = new RecordingTarget(_recordings, "SP-0164 channel", []),
            StartedAt = DateTimeOffset.Now.AddMinutes(-1),
            EndedAt = DateTimeOffset.Now,
            StagingDirectories = [_staging]
        };

        var saved = SegmentOf(RecordingFinisher.FinishAsync(segment, _log));

        // Not released: the finisher refuses to guess and leaves the file exactly where it is.
        Assert.Equal(SegmentFate.Stranded, saved.Fate);
        Assert.True(File.Exists(staged));
        Assert.Empty(Directory.GetFiles(_recordings));
    }

    [Fact]
    public void StalePartialsAreRemovedButFreshOnesAndRecordingsAreLeftAlone()
    {
        var stale = Path.Combine(_recordings, "old.mp4" + RecordingFinisher.PartialSuffix);
        var fresh = Path.Combine(_recordings, "new.mp4" + RecordingFinisher.PartialSuffix);
        var recording = Path.Combine(_recordings, "real.mp4");
        foreach (var file in new[] { stale, fresh, recording })
        {
            File.WriteAllText(file, "x");
        }

        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - RecordingFinisher.StalePartialAge - TimeSpan.FromMinutes(5));
        RecordingFinisher.RemoveStalePartials([_recordings], _log, DateTimeOffset.UtcNow);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(recording));
    }

    private static SegmentResult SegmentOf(Task<IReadOnlyList<SegmentResult>>? finish) =>
        finish is null
            ? throw new InvalidOperationException("no finish task")
            : finish.GetAwaiter().GetResult().Single();

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sp0164-finish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
