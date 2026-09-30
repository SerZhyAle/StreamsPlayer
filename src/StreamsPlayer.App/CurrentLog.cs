using System.Globalization;
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

    /// <summary>How much further the log may grow after a failed compaction before the next attempt (SP-0134).</summary>
    private const long CompactionRetryBytes = 1L * 1024 * 1024;

    private static readonly UTF8Encoding LogEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly DiagnosticPathRedactor _paths;
    private StreamWriter? _writer;
    private long _nextCompactionAt = MaximumSessionBytes;

    public CurrentLog(string directory)
    {
        _path = Path.Combine(directory, DiagnosticLogFiles.CurrentLogName);
        _paths = DiagnosticPathRedactor.ForCurrentUser(directory);
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

            // ReadWrite, not Write: compaction reads the tail back and rewrites it through this same
            // handle (SP-0134), so it never has to reopen a file someone else may be holding.
            var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
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
            // A failed diagnostic flush must not interrupt WPF shutdown.
            ReleaseWriter();
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

                _writer.WriteLine($"{DateTimeOffset.UtcNow:O} [{severity}] {Flatten(message)}");
                // AutoFlush is on, so the stream position is the file's real byte count - no estimate,
                // and no second syscall to ask for it.
                if (_writer.BaseStream.Position >= _nextCompactionAt)
                {
                    // Compact handles its own failures and keeps the writer; see its remarks.
                    Compact(_writer);
                }
            }
            catch (Exception)
            {
                // The line itself could not be written: logging has no recovery path, and continuing is
                // safer than masking the original application operation. The handle is released rather
                // than abandoned (SP-0134).
                ReleaseWriter();
            }
        }
    }

    private void ReleaseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // Disposing flushes, and the flush is what just failed; the handle is closed either way.
        }
        finally
        {
            _writer = null;
        }
    }

    /// <summary>
    /// Drop the middle part of the session log, keeping its head and tail (SP-0097). Caller holds <see cref="_gate"/>.
    /// </summary>
    /// <remarks>
    /// SP-0134: done in place, through the handle this instance already holds. It used to close the file
    /// and recreate it by name, and anything else holding <c>Current.log</c> at that moment - an editor, a
    /// virus scanner, the user copying it - made the recreate fail; the writer was already gone, so the
    /// session logged nothing more. The head is already where it belongs, so only the marker and the tail
    /// are written after it and the file is cut to that length. A failure is recorded at the end of the file
    /// and logging carries on; the next attempt waits for another <see cref="CompactionRetryBytes"/>, so a
    /// failing compaction is not re-tried on every line.
    /// </remarks>
    private void Compact(StreamWriter logWriter)
    {
        var stream = logWriter.BaseStream;
        var length = stream.Length;
        var headLen = Math.Min(RetainedHeadBytes, length);
        var tailStart = Math.Max(headLen, length - RetainedTailBytes);
        byte[] tail;
        try
        {
            tail = new byte[length - tailStart];
            stream.Seek(tailStart, SeekOrigin.Begin);
            stream.ReadExactly(tail);
        }
        catch (Exception exception)
        {
            // Nothing was changed yet: the file only grows past its ceiling until the next attempt.
            stream.Seek(0, SeekOrigin.End);
            CompactionFailed(logWriter, "read", exception, staleFrom: null);
            return;
        }

        var dropped = length - (headLen + tail.Length);
        try
        {
            // Head and tail are bytes Write already redacted; the tail is copied, not written anew.
            stream.Seek(headLen, SeekOrigin.Begin);
            logWriter.WriteLine();
            logWriter.WriteLine($"{DateTimeOffset.UtcNow:O} [Diag] {Flatten($"LOG COMPACTED | dropped_middle_bytes={dropped} | kept_head_bytes={headLen} | kept_tail_bytes={tail.Length}")}");
            logWriter.BaseStream.Write(tail);
            stream.SetLength(stream.Position);
            stream.Flush();
            _nextCompactionAt = MaximumSessionBytes;
        }
        catch (Exception exception)
        {
            // Part-way: the compacted content is in place up to here, and whatever follows it up to the
            // failure line is a stale copy of older lines. Say so where the reader will meet it.
            var staleFrom = SafePosition(stream);
            stream.Seek(0, SeekOrigin.End);
            CompactionFailed(logWriter, "rewrite", exception, staleFrom);
        }
    }

    private void CompactionFailed(StreamWriter logWriter, string stage, Exception exception, long? staleFrom)
    {
        _nextCompactionAt = logWriter.BaseStream.Length + CompactionRetryBytes;
        logWriter.WriteLine($"{DateTimeOffset.UtcNow:O} [Diag] {Flatten($"LOG COMPACTION FAILED | stage={stage} | err={exception.GetType().Name} | msg={exception.Message} | stale_from_byte={(staleFrom?.ToString(CultureInfo.InvariantCulture) ?? "none")} | next_attempt_at_bytes={_nextCompactionAt}")}");
    }

    private static long? SafePosition(Stream stream)
    {
        try
        {
            return stream.Position;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Full URLs are retained for measurement (SP-0040), minus the credentials they carry (SP-0123,
    // DIAGNOSTIC-REPORT rule 3), and paths keep their tail but lose the profile and data-directory roots
    // (SP-0137, the same rule's path half): this is the one sink every line passes through, so redacting
    // here covers every call site, including ones not written yet. Line breaks are flattened so each record
    // stays on one line. LogSinkRedactionSourceTests fails a WriteLine in this file that skips this method.
    private string Flatten(string message) =>
        _paths.Redact(CatalogUrlIdentity.RedactText(message)).ReplaceLineEndings(" | ");
}
