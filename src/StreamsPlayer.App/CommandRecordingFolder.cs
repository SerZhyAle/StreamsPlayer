using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>What <see cref="CommandRecordingFolder.Probe"/> found at the path a command named.</summary>
internal enum RecordingFolderState
{
    /// <summary>The folder exists, and a block was written and flushed in it.</summary>
    Ready,

    /// <summary>The path is a file.</summary>
    IsFile,

    /// <summary>SP-0279: the folder does not exist. A folder the caller names is never created (CAPTURE-OUTPUT 0.8; exit 11).</summary>
    Missing,

    /// <summary>The volume is full (exit 13).</summary>
    NoSpace,

    /// <summary>The folder cannot be created or written (exit 14).</summary>
    NotWritable
}

/// <summary>
/// SP-0273: the write test a recording command makes of the folder the caller named, before it asks a recorder to write there.
/// <see cref="CaptureFolders"/> tests a folder by creating an empty file, which a full volume still allows; this one writes and
/// flushes a block, so a full disk and an unwritable folder are told apart and told before the recording starts (exit 13 and 14).
/// Touches the disk: call it off the UI thread.
/// </summary>
internal static class CommandRecordingFolder
{
    /// <summary>How much the probe writes: more than a sector, small enough that a volume with room never notices.</summary>
    internal const int ProbeBytes = 64 * 1024;

    internal static RecordingFolderState Probe(string folder)
    {
        try
        {
            if (File.Exists(folder))
            {
                return RecordingFolderState.IsFile;
            }

            if (!Directory.Exists(folder))
            {
                return RecordingFolderState.Missing;
            }

            var probe = Path.Combine(folder, $"~probe-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.DeleteOnClose | FileOptions.WriteThrough);
            stream.Write(new byte[ProbeBytes]);
            stream.Flush(flushToDisk: true);
            return RecordingFolderState.Ready;
        }
        catch (Exception exception) when (CaptureFolders.IsFolderRefusal(exception))
        {
            return AppRecordCommands.ClassifyWrite(exception) == AppRecordWriteProblem.NoSpace
                ? RecordingFolderState.NoSpace
                : RecordingFolderState.NotWritable;
        }
    }

    /// <summary>The answer for a folder that is not ready.</summary>
    internal static AppCommandResponse Refusal(string requestId, RecordingFolderState state) => state switch
    {
        RecordingFolderState.IsFile => AppRecordCommands.OutIsFile(requestId),
        RecordingFolderState.Missing => AppRecordCommands.OutMissing(requestId),
        RecordingFolderState.NoSpace => AppRecordCommands.WriteFailed(requestId, AppRecordWriteProblem.NoSpace),
        _ => AppRecordCommands.WriteFailed(requestId, AppRecordWriteProblem.NotWritable)
    };
}
