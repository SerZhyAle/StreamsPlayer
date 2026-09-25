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

    /// <summary>
    /// How far a truncation cut may move to land on a line boundary. A cut inside a line can split a URL
    /// and hide its credentials from the redactor; a line longer than this is not a real log line.
    /// </summary>
    private const int LineAlignWindow = 64 * 1024;

    private static readonly UTF8Encoding LogEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static void AddLog(ZipArchive archive, string path, StringBuilder notes)
    {
        // FileShare.ReadWrite, not Read: the live session's writer holds this file with write access,
        // and a share mode that excludes it makes the current log unarchivable.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var name = Path.GetFileName(path);
        using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        if (source.Length <= MaxLogBytes)
        {
            WriteRedacted(entry, ReadAt(source, 0, source.Length));
            return;
        }

        var headBytes = Math.Min(HeadLogBytes, source.Length);
        var tailBytes = Math.Min(MaxLogBytes - headBytes, Math.Max(0, source.Length - headBytes));
        var head = ReadAt(source, 0, headBytes);
        head = head[..HeadCut(head)];
        // One byte before the tail says whether the tail already starts on a line boundary.
        var tailWithLead = ReadAt(source, source.Length - tailBytes - 1, tailBytes + 1);
        var tail = tailWithLead[TailCut(tailWithLead)..];
        var dropped = source.Length - (head.Length + tail.Length);
        notes.Append("log_truncated=").Append(name)
            .Append(" | kept_bytes=").Append((head.Length + tail.Length).ToString(CultureInfo.InvariantCulture))
            .Append(" | dropped_middle_bytes=").Append(dropped.ToString(CultureInfo.InvariantCulture))
            .Append("\r\n");

        WriteRedacted(entry, head);
        var marker = LogEncoding.GetBytes($"\r\n[Diag] LOG TRUNCATED | dropped_middle_bytes={dropped} | kept_head_bytes={head.Length} | kept_tail_bytes={tail.Length}\r\n");
        entry.Write(marker, 0, marker.Length);
        WriteRedacted(entry, tail);
    }

    /// <summary>
    /// SP-0123: every log is redacted again as it is packed. The live sink already redacts, but logs kept
    /// from an earlier version were written before it did, and they leave the machine in this archive.
    /// </summary>
    private static void WriteRedacted(Stream destination, ReadOnlySpan<byte> bytes)
    {
        var redacted = LogEncoding.GetBytes(CatalogUrlIdentity.RedactText(LogEncoding.GetString(bytes)));
        destination.Write(redacted, 0, redacted.Length);
    }

    /// <summary>Length of the head that ends on a line boundary, when one is near enough.</summary>
    private static int HeadCut(byte[] head)
    {
        var lastBreak = Array.LastIndexOf(head, (byte)'\n');
        return lastBreak >= 0 && head.Length - (lastBreak + 1) <= LineAlignWindow ? lastBreak + 1 : head.Length;
    }

    /// <summary>Offset in <paramref name="tailWithLead"/> where whole lines start; index 0 is the byte before the tail.</summary>
    private static int TailCut(byte[] tailWithLead)
    {
        if (tailWithLead[0] == (byte)'\n')
        {
            return 1;
        }

        var firstBreak = Array.IndexOf(tailWithLead, (byte)'\n', 1);
        return firstBreak >= 0 && firstBreak <= LineAlignWindow ? firstBreak + 1 : 1;
    }

    private static byte[] ReadAt(Stream source, long offset, long count)
    {
        // The live writer can still be appending, so read what was asked for and stop at the end.
        var buffer = new byte[count];
        source.Seek(offset, SeekOrigin.Begin);
        var total = 0;
        while (total < buffer.Length)
        {
            var read = source.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }
}
