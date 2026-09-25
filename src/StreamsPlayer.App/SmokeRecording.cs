using System.IO;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0121 R8: lets <c>scripts/smoke-playback.ps1</c> record through the shipping binary. When the gate sets
/// <c>STREAMSPLAYER_SMOKE_RECORD_SECONDS</c>, the first stream that goes live is recorded for that many seconds
/// and stopped, and the outcome is logged as one <c>SMOKE RECORD</c> line the script reads. The optional
/// <c>STREAMSPLAYER_SMOKE_RECORD_FOLDER</c> keeps the files out of the user's own recordings folder.
/// <para>Environment rather than a command-line option on purpose: the launch contract (<c>--url</c>/<c>--id</c>,
/// exactly two arguments) is shared with the shell integration and must not grow a test switch. An ordinary
/// launch never has the variable, so the product never records on its own.</para>
/// </summary>
internal static class SmokeRecording
{
    private const int MaximumSeconds = 120;

    private static int _claimed;

    /// <summary>How long to record, or null when the gate did not ask. Read on each call; cheap.</summary>
    internal static TimeSpan? Duration
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("STREAMSPLAYER_SMOKE_RECORD_SECONDS");
            return int.TryParse(raw, out var seconds) && seconds is > 0 and <= MaximumSeconds
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
    }

    /// <summary>The folder the gate wants recordings in, or null for the user's own setting.</summary>
    internal static string? Folder
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("STREAMSPLAYER_SMOKE_RECORD_FOLDER");
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    /// <summary>True exactly once per process: the first live stream records, later ones do not.</summary>
    internal static bool TryClaim() => Duration is not null && Interlocked.Exchange(ref _claimed, 1) == 0;

    /// <summary>The line the gate reads: what happened, where the file is and how big it is.</summary>
    internal static void Report(CurrentLog log, string kind, string fate, string? path)
    {
        long bytes = -1;
        try
        {
            if (path is not null && File.Exists(path))
            {
                bytes = new FileInfo(path).Length;
            }
        }
        catch (IOException)
        {
            // Size unknown; the gate treats -1 as a failed check.
        }

        log.Event("SMOKE RECORD", $"kind={kind}", $"fate={fate}", $"bytes={bytes}", $"path={path ?? "none"}");
    }
}
