using System.Diagnostics;
using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>SP-0121: where a recording is meant to end up, and what it is called after.</summary>
internal sealed record RecordingTarget(string Folder, string? ChannelTitle);

/// <summary>
/// SP-0121: one stretch of a recording the engine has stopped writing - ended by the user, by a playback re-open,
/// by a stop or by teardown. It says where the engine's file is, not what it is called: a LibVLC segment is
/// whatever new file appeared in its staging directories, a Flyleaf segment is the file it was told to write.
/// </summary>
internal sealed class RecordingSegment
{
    public required string Engine { get; init; }
    public required RecordingTarget Target { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>Staging directories to look in, most specific first. Empty for an engine that writes the target.</summary>
    public IReadOnlyList<string> StagingDirectories { get; init; } = [];

    /// <summary>Files that were already in the staging directories when the segment started - never its own.</summary>
    public IReadOnlySet<string> PreexistingFiles { get; init; } = new HashSet<string>();

    /// <summary>The file an engine writes directly, when it names it itself.</summary>
    public string? KnownFile { get; init; }

    /// <summary>
    /// True when the engine closed the file before handing the segment over (a re-open, a stop, teardown): the
    /// file either exists already or never will, so the finisher waits only briefly for it to appear.
    /// </summary>
    public bool ClosedByEngine { get; set; }

    public TimeSpan Length => EndedAt > StartedAt ? EndedAt - StartedAt : TimeSpan.Zero;
}

internal enum SegmentFate
{
    /// <summary>The file is in the recordings folder.</summary>
    Saved,

    /// <summary>A file exists but could not be put in the recordings folder; <see cref="SegmentResult.Path"/> says where it is.</summary>
    Stranded,

    /// <summary>The engine wrote nothing - the segment was too short to produce a file.</summary>
    Empty
}

internal sealed record SegmentResult(SegmentFate Fate, string? Path, TimeSpan Length, string? Error = null);

/// <summary>
/// SP-0121: finishes a segment - waits, bounded, for the engine to release its file and then moves it into the
/// recordings folder. Always on a worker thread and never under an engine's gate: the move is a copy when the
/// recordings folder is on another drive, and an hour of video is gigabytes (C-05). The fixed 80 ms pause and the
/// "newest file in a shared folder" guess it replaces could lose a file to a slow close or to another window (C-04).
/// </summary>
internal static class RecordingFinisher
{
    /// <summary>How long the engine gets to close a file after a stop it was asked for.</summary>
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a file that the engine already closed gets to appear - enough for a directory listing to catch up.</summary>
    internal static readonly TimeSpan ClosedAppearTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);
    private const int MaxMoveAttempts = 5;

    internal static Task<IReadOnlyList<SegmentResult>> FinishAsync(RecordingSegment segment, CurrentLog log) =>
        Task.Run(() => Finish(segment, log));

    private static IReadOnlyList<SegmentResult> Finish(RecordingSegment segment, CurrentLog log)
    {
        var clock = Stopwatch.StartNew();
        var files = WaitForRelease(segment, clock);
        if (files.Count == 0)
        {
            log.Event("RECORD FINISH", $"engine={segment.Engine}", "fate=empty", $"wait_ms={clock.ElapsedMilliseconds}");
            CleanStaging(segment);
            return [new SegmentResult(SegmentFate.Empty, null, segment.Length)];
        }

        var results = new List<SegmentResult>(files.Count);
        foreach (var (file, released) in files)
        {
            results.Add(FinishFile(segment, file, released, clock, log));
        }

        CleanStaging(segment);
        return results;
    }

    private static SegmentResult FinishFile(RecordingSegment segment, string file, bool released, Stopwatch clock, CurrentLog log)
    {
        if (!released)
        {
            log.Event("RECORD FINISH", $"engine={segment.Engine}", "fate=stranded", "reason=still_in_use", $"path={file}", $"wait_ms={clock.ElapsedMilliseconds}");
            return new SegmentResult(SegmentFate.Stranded, file, segment.Length, "still in use");
        }

        try
        {
            if (new FileInfo(file).Length == 0)
            {
                File.Delete(file);
                log.Event("RECORD FINISH", $"engine={segment.Engine}", "fate=empty", "reason=zero_bytes", $"path={file}");
                return new SegmentResult(SegmentFate.Empty, null, segment.Length);
            }

            if (segment.KnownFile is not null)
            {
                log.Event("RECORD FINISH", $"engine={segment.Engine}", "fate=saved", $"path={file}", $"wait_ms={clock.ElapsedMilliseconds}");
                return new SegmentResult(SegmentFate.Saved, file, segment.Length);
            }

            Directory.CreateDirectory(segment.Target.Folder);
            var name = RecordedBroadcastName.For(segment.Target.ChannelTitle, segment.StartedAt, Path.GetExtension(file));
            var moveClock = Stopwatch.StartNew();
            var destination = MoveIntoFolder(file, segment.Target.Folder, name);
            log.Event("RECORD FINISH", $"engine={segment.Engine}", "fate=saved", $"path={destination}",
                $"wait_ms={clock.ElapsedMilliseconds - moveClock.ElapsedMilliseconds}", $"move_ms={moveClock.ElapsedMilliseconds}");
            return new SegmentResult(SegmentFate.Saved, destination, segment.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.Event("RECORD FINISH", $"engine={segment.Engine}", "fate=stranded", $"path={file}", $"err={exception.Message}");
            return new SegmentResult(SegmentFate.Stranded, file, segment.Length, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Moves a file into a folder under a name that is free there, retrying when another move takes the same name
    /// between the check and the move. Returns the destination.
    /// </summary>
    internal static string MoveIntoFolder(string file, string folder, string fileName)
    {
        for (var attempt = 1; ; attempt++)
        {
            var destination = RecordedBroadcastWriter.ReserveUniquePath(folder, fileName);
            try
            {
                File.Move(file, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (attempt < MaxMoveAttempts && File.Exists(destination) && File.Exists(file))
            {
                // Lost the name to a concurrent move; reserve the next one.
            }
        }
    }

    /// <summary>
    /// The segment's files and whether each is released, once every file present is released or the wait is over.
    /// A released file is one this process can open with no sharing at all - the engine's write handle refuses that.
    /// </summary>
    private static IReadOnlyList<(string File, bool Released)> WaitForRelease(RecordingSegment segment, Stopwatch clock)
    {
        var appearDeadline = segment.ClosedByEngine ? ClosedAppearTimeout : ReleaseTimeout;
        while (true)
        {
            var files = Candidates(segment);
            if (files.Count > 0)
            {
                var states = files.Select(file => (file, IsReleased(file))).ToList();
                if (states.All(state => state.Item2) || clock.Elapsed >= ReleaseTimeout)
                {
                    return states;
                }
            }
            else if (clock.Elapsed >= appearDeadline)
            {
                return [];
            }

            Thread.Sleep(Poll);
        }
    }

    private static List<string> Candidates(RecordingSegment segment)
    {
        if (segment.KnownFile is { } known)
        {
            return File.Exists(known) ? [known] : [];
        }

        // Most specific directory first: a file found there belongs to this segment and nothing else, so the
        // shared instance directory is consulted only when the leg's own directory has nothing.
        foreach (var directory in segment.StagingDirectories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var found = Directory.GetFiles(directory)
                    .Where(file => !segment.PreexistingFiles.Contains(file))
                    .OrderBy(File.GetCreationTimeUtc)
                    .ToList();
                if (found.Count > 0)
                {
                    return found;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unlistable now; the next poll tries again.
            }
        }

        return [];
    }

    internal static bool IsReleased(string file)
    {
        try
        {
            using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CleanStaging(RecordingSegment segment)
    {
        if (segment.StagingDirectories.Count > 0)
        {
            RecordingStaging.RemoveIfEmpty(segment.StagingDirectories[0]);
        }
    }
}
