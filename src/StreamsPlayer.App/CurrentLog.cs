using System.IO;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed class CurrentLog : IDisposable
{
    /// <summary>Largest the live session log may grow before its oldest half is dropped.</summary>
    /// <remarks>
    /// SP-0069: this was the one resource in the application with no ceiling inside a session.
    /// <see cref="DiagnosticLogFiles"/> rotates by launch count, never by size, so a session that stayed
    /// open grew this file for as long as it ran - and an open player window writes a STATS line every
    /// two seconds, so "as long as it ran" is a real rate rather than a theoretical one.
    /// Dropping the *oldest* content rather than refusing new lines is deliberate and matches what
    /// <see cref="DiagnosticArchiveBuilder"/> already does when it packs an oversize log: it seeks past
    /// the leading bytes and keeps the tail, because the end of the log is what explains what just
    /// happened. A ceiling that kept the head would bound the file and destroy its value.
    /// </remarks>
    private const long MaximumSessionBytes = 16L * 1024 * 1024;

    /// <summary>Initial bytes (startup/configuration/first connects) retained across compaction (SP-0097).</summary>
    private const long RetainedHeadBytes = 1L * 1024 * 1024;

    /// <summary>Recent bytes (the tail leading up to the failure) retained across compaction (SP-0097).</summary>
    private const long RetainedTailBytes = 7L * 1024 * 1024;

    private static readonly UTF8Encoding LogEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _path;
    private StreamWriter? _writer;

    public CurrentLog(string directory)
    {
        _path = Path.Combine(directory, DiagnosticLogFiles.CurrentLogName);
        try
        {
            Directory.CreateDirectory(directory);
            // SP-0040/SP-0055: retire the finished session and keep the last ten. The session worth
            // mailing to the author is usually one that already ended, so replacing the log on launch
            // destroyed the evidence on the way to the send button - and keeping only one spare lost it
            // again after two restarts. Rotation is best-effort inside the helper: losing retention is
            // acceptable, failing to open the current log is not.
            var previousRetained =
                DiagnosticLogFiles.Rotate(directory) == LogRotationOutcome.PreviousLogRetained;
            if (previousRetained)
            {
                // SP-0085: the previous instance still owns the current log - the user closed the window
                // and reopened the app before the old process let go of the handle. Opening that name
                // throws, and this constructor's catch turned that into a session that played a stream
                // for two minutes without writing a line. Take a session name of our own instead: it is
                // already the name this run would have been retired under, and the archive, the ordering
                // and the prune all discover it. Choosing by the rotation outcome rather than by a failed
                // open also keeps FileMode.Create away from a log that was never retired.
                _path = DiagnosticLogFiles.ReserveSessionPath(directory, DateTime.Now);
            }

            var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, LogEncoding) { AutoFlush = true };
            if (previousRetained)
            {
                // The absence of a file is not a diagnosis. This is, and it is the first record here.
                Event("LOG ROTATION FAILED",
                    $"previous={DiagnosticLogFiles.CurrentLogName}",
                    $"session={Path.GetFileName(_path)}");
            }
        }
        catch (Exception)
        {
            // Diagnostics must not prevent the application from opening when local storage is unavailable.
        }
    }

    public void Information(string message) => Write("Information", message);

    // Measurable diagnostic record: a category plus KEY=value fields joined by " | " so lines stay greppable.
    public void Event(string category, params string[] fields) =>
        Write("Diag", fields.Length == 0 ? category : $"{category} | {string.Join(" | ", fields)}");

    public void Error(string operation, Exception exception) => Write("Error", $"{operation}: {exception}");

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception)
            {
                // A failed diagnostic flush must not interrupt WPF shutdown.
            }
            finally
            {
                _writer = null;
            }
        }
    }

    private void Write(string severity, string message)
    {
        lock (_gate)
        {
            try
            {
                if (_writer is null)
                {
                    return;
                }

                _writer.WriteLine($"{DateTimeOffset.UtcNow:O} [{severity}] {Flatten(message)}");                // AutoFlush is on, so the stream position is the file's real byte count - no estimate,
                // and no second syscall to ask for it.
                if (_writer.BaseStream.Position >= MaximumSessionBytes)
                {
                    Compact();
                }
            }
            catch (Exception)
            {
                // Logging has no recovery path; continuing is safer than masking the original application operation.
                _writer = null;
            }
        }
    }

    /// <summary>Drop the middle part of the session log, keeping its head and tail (SP-0097). Caller holds <see cref="_gate"/>.</summary>
    private void Compact()
    {
        byte[] head;
        byte[] tail;
        long dropped;
        try
        {
            // Read before closing the writer: a failure here must leave logging exactly as it was, so the
            // ceiling degrades into "the file grows" rather than "the session stops being diagnosable".
            // FileShare.ReadWrite because this process still holds the file open for writing.
            using var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var headLen = (int)Math.Min(RetainedHeadBytes, source.Length);
            head = new byte[headLen];
            source.Seek(0, SeekOrigin.Begin);
            source.ReadExactly(head);

            var tailLen = (int)Math.Min(RetainedTailBytes, Math.Max(0, source.Length - headLen));
            var tailStart = Math.Max(headLen, source.Length - tailLen);
            tail = new byte[source.Length - tailStart];
            source.Seek(tailStart, SeekOrigin.Begin);
            source.ReadExactly(tail);
            dropped = source.Length - (head.Length + tail.Length);
        }
        catch (Exception)
        {
            return;
        }

        _writer?.Dispose();
        _writer = null;
        var replacement = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(replacement, LogEncoding) { AutoFlush = true };
        // Head and tail are bytes Write already redacted; they are copied, not written anew.
        writer.BaseStream.Write(head);
        writer.WriteLine();
        writer.WriteLine($"{DateTimeOffset.UtcNow:O} [Diag] {Flatten($"LOG COMPACTED | dropped_middle_bytes={dropped} | kept_head_bytes={head.Length} | kept_tail_bytes={tail.Length}")}");
        writer.BaseStream.Write(tail);
        writer.BaseStream.Flush();
        _writer = writer;
    }

    // Full URLs are retained for measurement (SP-0040), minus the credentials they carry (SP-0123,
    // DIAGNOSTIC-REPORT rule 3): this is the one sink every line passes through, so redacting here covers
    // every call site, including ones not written yet. Line breaks are flattened so each record stays on
    // one line. LogSinkRedactionSourceTests fails a WriteLine in this file that skips this method.
    private static string Flatten(string message) =>
        CatalogUrlIdentity.RedactText(message).ReplaceLineEndings(" | ");
}
