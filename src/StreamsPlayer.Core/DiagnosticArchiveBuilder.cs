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

    /// <summary>
    /// How the mail body names the archive (SP-0174, <c>DIAGNOSTIC-REPORT</c> rule 3): by file name, with
    /// the folder reduced to its alias. The body leaves the machine inside a draft the user may store,
    /// quote or forward, so it must not carry a path under the user's profile; the on-screen confirmation
    /// keeps the real path, because that is what the user needs to find the file.
    /// </summary>
    public static string DescribePathForMail(string archivePath, DiagnosticPathRedactor? paths = null)
    {
        paths ??= DiagnosticPathRedactor.ForCurrentUser(null);
        return paths.Redact(archivePath);
    }

    /// <summary>
    /// Writes the archive and returns its full path. Failures propagate: the caller owns the
    /// user-visible message, and a silently empty archive is worse than an error (SP-0040 decision 5).
    /// </summary>
    /// <param name="paths">
    /// Path redaction (SP-0137); defaults to the running user's profile with <paramref name="stateDirectory"/> as the
    /// data directory. Tests pass their own roots.
    /// </param>
    public static string Build(
        string stateDirectory,
        string outputDirectory,
        string summaryText,
        DateTimeOffset utcNow,
        DiagnosticPathRedactor? paths = null)
    {
        paths ??= DiagnosticPathRedactor.ForCurrentUser(stateDirectory);
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(outputDirectory, $".{ArchivePrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            // WriteThrough (SP-0175): the finished archive is renamed to its final name right below, and a
            // rename journalled ahead of unflushed data would leave the user a zero-filled ZIP after a
            // power cut.
            using (var stream = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var log in DiagnosticLogFiles.ExistingLogs(stateDirectory))
                {
                    AddLog(archive, log, paths);
                }

                AddText(archive, SummaryEntryName, summaryText);
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

    private static readonly UTF8Encoding LogEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static void AddLog(ZipArchive archive, string path, DiagnosticPathRedactor paths)
    {
        // FileShare.ReadWrite, not Read: the live session's writer holds this file with write access,
        // and a share mode that excludes it makes the current log unarchivable.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var name = Path.GetFileName(path);
        using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        var sourceBytes = source.Length;
        if (sourceBytes <= MaxLogBytes)
        {
            WriteRedacted(entry, ReadAt(source, 0, sourceBytes), paths);
            return;
        }

        // Retained logs may predate sink redaction. A raw tail can start inside a multiline secret;
        // bounded export cannot safely sanitize that source (DIAGNOSTIC-REPORT section 8 C, 0.14).
        var marker = LogEncoding.GetBytes("[Diag] LOG OMITTED | reason=oversized_untrusted | source_bytes=" +
            sourceBytes.ToString(CultureInfo.InvariantCulture) + "\r\n");
        entry.Write(marker, 0, marker.Length);
    }

    /// <summary>
    /// SP-0123: every log is redacted again as it is packed. The live sink already redacts, but logs kept
    /// from an earlier version were written before it did, and they leave the machine in this archive.
    /// SP-0137: the same pass replaces the profile and data-directory roots in every path it finds.
    /// </summary>
    private static void WriteRedacted(Stream destination, ReadOnlySpan<byte> bytes, DiagnosticPathRedactor paths)
    {
        var redacted = LogEncoding.GetBytes(paths.Redact(ExchangeDiagnosticRedactor.Redact(CatalogUrlIdentity.RedactText(DiagnosticValueRedactor.Redact(LogEncoding.GetString(bytes))))));
        destination.Write(redacted, 0, redacted.Length);
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
