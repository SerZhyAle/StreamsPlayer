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
    public const string SummaryEntryName = "environment.txt";
    public const string ArchivePrefix = "StreamsPlayer-logs-";
    private const int MaxNameAttempts = 100;

    /// <summary>Per-log ceiling (SP-0040, SP-0097). Matches CurrentLog.MaximumSessionBytes so a healthy session is never truncated on export.</summary>
    public const long MaxLogBytes = 16L * 1024 * 1024;

    /// <summary>SP-0097: Initial bytes (startup/configuration) retained when an oversize log is truncated.</summary>
    public const long HeadLogBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Writes the archive and returns its full path. Failures propagate: the caller owns the
    /// user-visible message, and a silently empty archive is worse than an error (SP-0040 decision 5).
    /// </summary>
    public static string Build(string stateDirectory, string outputDirectory, string summaryText, DateTimeOffset utcNow)
    {
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(outputDirectory, $".{ArchivePrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            var notes = new StringBuilder();
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var log in DiagnosticLogFiles.ExistingLogs(stateDirectory))
                {
                    AddLog(archive, log, notes);
                }

                AddText(archive, SummaryEntryName, summaryText + notes);
            }

            return MoveToUniqueArchivePath(temporaryPath, outputDirectory, utcNow);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static string MoveToUniqueArchivePath(string temporaryPath, string outputDirectory, DateTimeOffset utcNow)
    {
        var stem = $"{ArchivePrefix}{utcNow.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : $"-{attempt}";
            var destination = Path.Combine(outputDirectory, $"{stem}{suffix}.zip");
            try
            {
                File.Move(temporaryPath, destination);
                return destination;
            }
            catch (IOException) when (File.Exists(destination))
            {
                // A second request can race this one between choosing a name and moving the completed ZIP.
            }
        }

        throw new IOException($"No free archive name in '{outputDirectory}'.");
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The write failure remains the useful exception. The temporary file has a private random name
            // and was never presented as an archive to the user.
        }
    }

    private static void AddLog(ZipArchive archive, string path, StringBuilder notes)
    {
        // FileShare.ReadWrite, not Read: the live session's writer holds this file with write access,
        // and a share mode that excludes it makes the current log unarchivable.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var name = Path.GetFileName(path);
        if (source.Length > MaxLogBytes)
        {
            var headBytes = Math.Min(HeadLogBytes, source.Length);
            var tailBytes = Math.Min(MaxLogBytes - headBytes, Math.Max(0, source.Length - headBytes));
            var dropped = source.Length - (headBytes + tailBytes);
            notes.Append("log_truncated=").Append(name)
                .Append(" | kept_bytes=").Append((headBytes + tailBytes).ToString(CultureInfo.InvariantCulture))
                .Append(" | dropped_middle_bytes=").Append(dropped.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");

            using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            source.Seek(0, SeekOrigin.Begin);
            CopyExact(source, entry, headBytes);
            var marker = Encoding.UTF8.GetBytes($"\r\n[Diag] LOG TRUNCATED | dropped_middle_bytes={dropped} | kept_head_bytes={headBytes} | kept_tail_bytes={tailBytes}\r\n");
            entry.Write(marker, 0, marker.Length);
            source.Seek(source.Length - tailBytes, SeekOrigin.Begin);
            CopyExact(source, entry, tailBytes);
            return;
        }

        using var normalEntry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        source.CopyTo(normalEntry);
    }

    private static void CopyExact(Stream source, Stream destination, long count)
    {
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }
}
