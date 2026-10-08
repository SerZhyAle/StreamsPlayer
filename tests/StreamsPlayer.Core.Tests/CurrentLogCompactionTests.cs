using System.Text;
using System.Text.RegularExpressions;
using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// S8-4: compaction keeps the head and the tail of an oversize session log. Both cuts used to be plain byte
/// offsets, so a log of non-ASCII lines kept a torn record at each seam - and a torn UTF-8 sequence that no
/// decoder reads back as the text that was written. The App's <c>CurrentLog</c> is compiled into this project.
/// </summary>
public sealed class CurrentLogCompactionTests
{
    private static readonly Regex RecordStart = new(@"^\d{4}-\d{2}-\d{2}T", RegexOptions.CultureInvariant);

    [Fact]
    public void CompactionCutsAtLineBoundariesAndLeavesValidUtf8()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            // 2 + 3 bytes per pair: a 1 MiB / 7 MiB byte offset never lands on a line start, and lands inside
            // a multi-byte sequence about as often as not.
            var message = string.Concat(Enumerable.Repeat("é日", 2500));
            using (var log = new CurrentLog(directory))
            {
                // About 12.5 KB a line: the 16 MiB ceiling is crossed near line 1340, and the lines after
                // it prove logging carries on past the cut without reaching the ceiling again.
                for (var line = 0; line < 1400; line++)
                {
                    log.Information($"{line} {message}");
                }

                log.Information("after compaction");
            }

            var bytes = File.ReadAllBytes(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName));
            Assert.True(bytes.Length < 12L * 1024 * 1024, $"the log was not compacted ({bytes.Length} bytes)");

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            Assert.Contains("LOG COMPACTED", text, StringComparison.Ordinal);
            Assert.EndsWith("after compaction" + Environment.NewLine, text, StringComparison.Ordinal);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var record = line.TrimEnd('\r');
                Assert.True(
                    RecordStart.IsMatch(record),
                    $"a kept line does not start at a record boundary: {record[..Math.Min(40, record.Length)]}");
            }
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
