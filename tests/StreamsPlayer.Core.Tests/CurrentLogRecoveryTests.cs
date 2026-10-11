using System.Text;
using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0212 (L23): one failed log write used to release the writer and end the session's logging for
/// good. A session that once logged now reopens its file on the next line and appends, keeping what it
/// already wrote. The opener is <see cref="CurrentLog"/>'s own seam for exactly this - the one way to
/// exercise a write that fails mid-session - and stays internal on the SP-0052 terms.
/// </summary>
public sealed class CurrentLogRecoveryTests
{
    /// <summary>A real file stream that refuses to hold the one line the test names, like a full disk would.</summary>
    private sealed class FaultingStream(FileStream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Encoding.UTF8.GetString(buffer, offset, count).Contains("the line that must fail", StringComparison.Ordinal))
            {
                throw new IOException("Simulated disk full.");
            }

            inner.Write(buffer, offset, count);
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void AFailedWriteReopensTheLogOnTheNextLineInsteadOfEndingTheSessionLogging()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DiagnosticLogFiles.CurrentLogName);
            Stream Opener(string path, FileMode mode) => new FaultingStream(
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read));

            using (var log = new CurrentLog(directory, Opener))
            {
                log.Information("first line");
                log.Information("the line that must fail");
                log.Information("the line after the failure");
            }

            var text = File.ReadAllText(path);
            Assert.Contains("first line", text, StringComparison.Ordinal);
            Assert.DoesNotContain("must fail", text, StringComparison.Ordinal);
            Assert.Contains("LOG REOPENED", text, StringComparison.Ordinal);
            Assert.Contains("the line after the failure", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ASessionThatNeverOpenedALogStaysSilentRatherThanOpeningOneLater()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            // An opener that fails the way an unavailable disk does: the constructor absorbs it, and
            // the session never has a writer to recover.
            Stream Refused(string path, FileMode mode) => throw new IOException("Simulated storage unavailable.");
            using (var log = new CurrentLog(directory, Refused))
            {
                log.Information("any line");
            }

            Assert.False(File.Exists(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
