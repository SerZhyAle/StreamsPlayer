using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Packs the session logs and the environment summary into one mailable archive (SP-0040).
/// </summary>
/// <remarks>
/// <para>
/// Nothing else goes in. The persisted catalog state sits in the same folder and holds the user's own
/// MANUAL/IMPORTED URLs, their pins and their listening history; an archive the user mails to the
/// author must not carry it (SP-0040 criterion 2).
/// </para>
/// <para>
/// The running session holds the current log open, so the obvious call - packing the directory - is
/// the wrong one: each log is streamed in with a share mode that tolerates the live writer.
/// </para>
/// </remarks>
public static class DiagnosticArchiveBuilder
{
    public const string ArchiveFolderName = "reports";
    public const string SummaryEntryName = "environment.txt";
    public const string ArchivePrefix = "StreamsPlayer-logs-";

    /// <summary>Per-log ceiling. The end of a log holds the failure, so an oversized log keeps its tail.</summary>
    public const long MaxLogBytes = 2L * 1024 * 1024;

    /// <summary>
    /// Writes the archive and returns its full path. Failures propagate: the caller owns the
    /// user-visible message, and a silently empty archive is worse than an error (SP-0040 decision 5).
    /// </summary>
    public static string Build(string stateDirectory, string summaryText, DateTimeOffset utcNow)
    {
        var folder = Path.Combine(stateDirectory, ArchiveFolderName);
        Directory.CreateDirectory(folder);
        RemovePreviousArchives(folder);

        var path = Path.Combine(
            folder,
            $"{ArchivePrefix}{utcNow.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip");
        var notes = new StringBuilder();
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var log in DiagnosticLogFiles.ExistingLogs(stateDirectory))
            {
                AddLog(archive, log, notes);
            }

            AddText(archive, SummaryEntryName, summaryText + notes);
        }

        return path;
    }

    private static void RemovePreviousArchives(string folder)
    {
        foreach (var stale in Directory.GetFiles(folder, $"{ArchivePrefix}*.zip"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An archive still open in the user's mail client cannot be deleted; a leftover file is
                // not worth failing the report the user asked for.
            }
        }
    }

    private static void AddLog(ZipArchive archive, string path, StringBuilder notes)
    {
        // FileShare.ReadWrite, not Read: the live session's writer holds this file with write access,
        // and a share mode that excludes it makes the current log unarchivable.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var name = Path.GetFileName(path);
        var skipped = 0L;
        if (source.Length > MaxLogBytes)
        {
            skipped = source.Length - MaxLogBytes;
            source.Seek(skipped, SeekOrigin.Begin);
            notes.Append("log_truncated=").Append(name)
                .Append(" | kept_bytes=").Append(MaxLogBytes.ToString(CultureInfo.InvariantCulture))
                .Append(" | dropped_leading_bytes=").Append(skipped.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");
        }

        using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        source.CopyTo(entry);
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }
}
