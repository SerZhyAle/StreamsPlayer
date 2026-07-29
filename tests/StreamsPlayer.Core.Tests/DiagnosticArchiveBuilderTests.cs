using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0040 phase 03: what the mailed archive contains - and, more importantly, what it does not.
/// </summary>
public sealed class DiagnosticArchiveBuilderTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 7, 30, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Build_PacksBothSessionLogsAndTheSummary()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "current session");
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.PreviousLogName), "previous session");

            var path = DiagnosticArchiveBuilder.Build(directory, "app_version=26.0730.0012\r\n", Stamp);

            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(
                [DiagnosticLogFiles.CurrentLogName, DiagnosticLogFiles.PreviousLogName, DiagnosticArchiveBuilder.SummaryEntryName],
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal("previous session", ReadEntry(archive, DiagnosticLogFiles.PreviousLogName));
            Assert.Equal("app_version=26.0730.0012\r\n", ReadEntry(archive, DiagnosticArchiveBuilder.SummaryEntryName));
            Assert.EndsWith("StreamsPlayer-logs-20260730-010203.zip", path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Build_WithNoPreviousLog_StillProducesAnArchive()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "only session");

            using var archive = ZipFile.OpenRead(DiagnosticArchiveBuilder.Build(directory, "summary", Stamp));

            Assert.Equal(2, archive.Entries.Count);
            Assert.Contains(archive.Entries, entry => entry.FullName == DiagnosticLogFiles.CurrentLogName);
        });
    }

    // Criterion 2's negative half: the state file lives in this very directory and holds the user's own
    // channels, pins and history. It must never be swept into a report they mail to another person.
    [Fact]
    public void Build_NeverPacksTheCatalogStateOrTheAtlas()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "log");
            File.WriteAllText(Path.Combine(directory, "catalog-state.json"), "{\"channels\":[]}");
            File.WriteAllBytes(Path.Combine(directory, "favicon-atlas-abc.png"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(directory, "grid-previews"));

            using var archive = ZipFile.OpenRead(DiagnosticArchiveBuilder.Build(directory, "summary", Stamp));

            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("catalog-state"));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("favicon-atlas"));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("grid-previews"));
        });
    }

    // The live session holds the current log open exactly like this; a share mode that ignored the
    // running writer would make the one log worth sending the one log that cannot be packed.
    [Fact]
    public void Build_ArchivesALogHeldOpenByTheRunningSession()
    {
        RunInTempDirectory(directory =>
        {
            var logPath = Path.Combine(directory, DiagnosticLogFiles.CurrentLogName);
            using var writer = new StreamWriter(
                new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            writer.WriteLine("PLAYBACK STALL | count=1");

            using var archive = ZipFile.OpenRead(DiagnosticArchiveBuilder.Build(directory, "summary", Stamp));

            Assert.Contains("PLAYBACK STALL", ReadEntry(archive, DiagnosticLogFiles.CurrentLogName));
        });
    }

    [Fact]
    public void Build_Twice_LeavesExactlyOneArchive()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "log");

            DiagnosticArchiveBuilder.Build(directory, "summary", Stamp);
            var second = DiagnosticArchiveBuilder.Build(directory, "summary", Stamp.AddMinutes(1));

            var archives = Directory.GetFiles(
                Path.Combine(directory, DiagnosticArchiveBuilder.ArchiveFolderName),
                $"{DiagnosticArchiveBuilder.ArchivePrefix}*.zip");
            Assert.Equal([second], archives);
        });
    }

    [Fact]
    public void Build_TruncatesAnOversizedLogToItsTailAndSaysSo()
    {
        RunInTempDirectory(directory =>
        {
            var line = new string('x', 1024) + "\r\n";
            var text = new StringBuilder();
            while (text.Length < DiagnosticArchiveBuilder.MaxLogBytes + 4096)
            {
                text.Append(line);
            }

            text.Append("FINAL LINE\r\n");
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), text.ToString());

            using var archive = ZipFile.OpenRead(DiagnosticArchiveBuilder.Build(directory, "summary\r\n", Stamp));

            var packed = ReadEntry(archive, DiagnosticLogFiles.CurrentLogName);
            Assert.Equal(DiagnosticArchiveBuilder.MaxLogBytes, packed.Length);
            Assert.EndsWith("FINAL LINE\r\n", packed, StringComparison.Ordinal);
            Assert.Contains("log_truncated=Current.log", ReadEntry(archive, DiagnosticArchiveBuilder.SummaryEntryName));
        });
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void RunInTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
