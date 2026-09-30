using System.IO;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0164: whether a recording's engine is writing, read from the engine's own staging rather than from
/// whether it accepted the start. LibVLC accepts the record request and may refuse it later on the input
/// thread, and its only visible truth is the <c>vlc-record-*</c> file appearing in the segment's staging
/// directories - so a segment with no file of its own past the grace window is a recording that is not
/// happening (S43-02). Flyleaf reports its player's own flag and does not need this.
/// <para>WPF- and VLC-free on purpose: the test project compiles this file and works a staging
/// directory with its hands, as the engine double.</para>
/// </summary>
internal static class RecordingWriteProbe
{
    /// <summary>
    /// How long a just-accepted start may take to show its file. One stats tick is 2 s; the file of a
    /// recording that was truly accepted appears well inside this, and one that never appears is caught on
    /// the next tick after it.
    /// </summary>
    internal static readonly TimeSpan AppearGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True while a file of this segment's own is visible in staging, or while the start is young enough
    /// that its file may still be about to appear. A directory that cannot be listed counts as no evidence:
    /// the answer is corrected on the next tick, and the finisher still waits for the file.
    /// </summary>
    internal static bool IsWriting(
        DateTimeOffset startedAt,
        IReadOnlyList<string> stagingDirectories,
        IReadOnlySet<string> preexistingFiles,
        DateTimeOffset now)
    {
        if (now - startedAt < AppearGrace)
        {
            return true;
        }

        foreach (var directory in stagingDirectories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                if (Directory.GetFiles(directory).Any(file => !preexistingFiles.Contains(file)))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unlistable now; the next tick tries again.
            }
        }

        return false;
    }
}
