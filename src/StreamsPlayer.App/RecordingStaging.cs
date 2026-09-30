using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0121: where an engine writes a recording before it is finished, and what happens to anything a crash left
/// there. The folder used to be one directory every window shared and nothing ever emptied: two windows recording
/// at once could take each other's file, and a file the move failed on stayed hidden for good (C-04).
/// <para>Each engine instance now gets a directory of its own under <see cref="Root"/>, and inside it one
/// sub-directory per playback leg, so a file's location alone says which recording it belongs to. The whole tree
/// is private to this process: the product is single-instance, so at startup nothing in it can still be in use,
/// and <see cref="HandOverLeftoversAsync"/> gives every file it finds to the user.</para>
/// </summary>
internal static class RecordingStaging
{
    internal static string Root => Path.Combine(AppPaths.DataDirectory, "RecordingsStaging");

    /// <summary>
    /// A fresh directory name for one engine instance. Not created here - an engine that never records leaves no
    /// trace - and never reused, so a directory left behind by a crash cannot collect a later session's files.
    /// </summary>
    internal static string NewInstanceDirectory()
    {
        var directory = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24]);
        Owned.TryAdd(Path.GetFullPath(directory), 0);
        return directory;
    }

    /// <summary>
    /// Directories this process handed to an engine. The hand-over skips them: a player opened by the launch (a
    /// startup resume, a <c>--url</c>) can already be recording while the hand-over runs, and its file is not a
    /// leftover. Never removed - an engine's directory stays this session's for the life of the process.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Owned = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsOwnedByThisProcess(string file) =>
        Owned.Keys.Any(directory => file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Moves every file a previous session left in staging into the recordings folder, removes the emptied
    /// directories, and reports what it did. Runs on a worker thread; a single file that cannot be moved is
    /// counted and left where it is, and the result names that place.
    /// <para>SP-0179: only the LibVLC video engine stages, so a leftover is a <c>stream_video</c> and walks the video
    /// chain. It is renamed by the contract's grammar with no label - the channel it came from is not known any
    /// more - and stamped with the file's creation time, the nearest record of when the capture started.</para>
    /// </summary>
    internal static Task<StagingHandOver> HandOverLeftoversAsync(IReadOnlyList<string> chain, CurrentLog log) => Task.Run(() =>
    {
        // SP-0164: first, the partial copies a killed session left in the recordings folders - their staged
        // originals are what this hand-over files away, and a partial beside a recording's final name is
        // nothing the user should ever see. Runs even when staging itself is empty.
        RecordingFinisher.RemoveStalePartials(chain, log, DateTimeOffset.Now);

        var root = Root;
        if (!Directory.Exists(root))
        {
            return StagingHandOver.None;
        }

        var moved = 0;
        var stranded = 0;
        var targetFolder = chain[0];
        foreach (var file in SafeEnumerateFiles(root).Where(file => !IsOwnedByThisProcess(Path.GetFullPath(file))))
        {
            try
            {
                if (new FileInfo(file).Length == 0)
                {
                    File.Delete(file);
                    continue;
                }

                var name = CaptureFileName.For(
                    CaptureKind.StreamVideo, new DateTimeOffset(File.GetCreationTime(file)), null, Path.GetExtension(file));
                var (destination, skipped) = RecordingFinisher.MoveIntoFirst(file, chain, name);
                targetFolder = Path.GetDirectoryName(destination) ?? targetFolder;
                log.Event("RECORD RECOVERED", $"from={file}", $"to={destination}", $"skipped={skipped ?? "none"}");
                moved++;
            }
            catch (Exception exception) when (CaptureFolders.IsFolderRefusal(exception))
            {
                log.Event("RECORD RECOVERED", "ok=false", $"path={file}", $"err={exception.Message}");
                stranded++;
            }
        }

        RemoveEmptyDirectories(root); // never a directory an engine of this process owns - it may be about to write
        return new StagingHandOver(moved, stranded, targetFolder, root);
    });

    /// <summary>Deletes <paramref name="directory"/> and its empty parents up to <see cref="Root"/>, if they are empty.</summary>
    internal static void RemoveIfEmpty(string? directory)
    {
        var root = Path.GetFullPath(Root);
        var current = directory is null ? null : Path.GetFullPath(directory);
        while (current is not null && current.Length > root.Length && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    return;
                }

                Directory.Delete(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        try
        {
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Where(directory => !IsOwnedByThisProcess(Path.GetFullPath(directory) + Path.DirectorySeparatorChar)
                    && !Owned.ContainsKey(Path.GetFullPath(directory)))
                .OrderByDescending(d => d.Length))
            {
                RemoveIfEmpty(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be listed is left alone; the next launch tries again.
        }
    }
}

/// <summary>What the startup hand-over found: files moved to the recordings folder, and files it could not move.</summary>
internal sealed record StagingHandOver(int Moved, int Stranded, string TargetFolder, string StagingRoot)
{
    internal static StagingHandOver None { get; } = new(0, 0, string.Empty, string.Empty);
}
