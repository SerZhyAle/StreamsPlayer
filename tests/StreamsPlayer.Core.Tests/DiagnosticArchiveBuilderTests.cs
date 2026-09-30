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
    public void Build_PacksEveryRetainedSessionLogAndTheSummary()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "current session");
            File.WriteAllText(Path.Combine(directory, "Session-20260730-0100.log"), "previous session");
            File.WriteAllText(Path.Combine(directory, "Session-20260729-2359.log"), "older session");

            var outputDirectory = Path.Combine(directory, "saved-files");
            var path = DiagnosticArchiveBuilder.Build(directory, outputDirectory, "app_version=26.0730.0012\r\n", Stamp);

            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(
                [DiagnosticLogFiles.CurrentLogName, "Session-20260729-2359.log", "Session-20260730-0100.log", DiagnosticArchiveBuilder.SummaryEntryName],
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal("previous session", ReadEntry(archive, "Session-20260730-0100.log"));
            Assert.Equal("app_version=26.0730.0012\r\n", ReadEntry(archive, DiagnosticArchiveBuilder.SummaryEntryName));
            Assert.EndsWith("StreamsPlayer-logs-20260730-010203.zip", path, StringComparison.Ordinal);
            Assert.StartsWith(outputDirectory, path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Build_WithNoPreviousLog_StillProducesAnArchive()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "only session");

            using var archive = ZipFile.OpenRead(Build(directory, "summary", Stamp));

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

            using var archive = ZipFile.OpenRead(Build(directory, "summary", Stamp));

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

            using var archive = ZipFile.OpenRead(Build(directory, "summary", Stamp));

            Assert.Contains("PLAYBACK STALL", ReadEntry(archive, DiagnosticLogFiles.CurrentLogName));
        });
    }

    [Fact]
    public void Build_Twice_PreservesExistingFilesAndCreatesUniqueArchives()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "log");

            var outputDirectory = Path.Combine(directory, "saved-files");
            Directory.CreateDirectory(outputDirectory);
            var existingArchive = Path.Combine(outputDirectory, $"{DiagnosticArchiveBuilder.ArchivePrefix}old.zip");
            var unrelatedFile = Path.Combine(outputDirectory, "keep-me.txt");
            File.WriteAllText(existingArchive, "existing archive");
            File.WriteAllText(unrelatedFile, "user file");

            var first = DiagnosticArchiveBuilder.Build(directory, outputDirectory, "summary", Stamp);
            var second = DiagnosticArchiveBuilder.Build(directory, outputDirectory, "summary", Stamp);

            var archives = Directory.GetFiles(
                outputDirectory,
                $"{DiagnosticArchiveBuilder.ArchivePrefix}*.zip");
            Assert.Equal(3, archives.Length);
            Assert.Contains(first, archives);
            Assert.Contains(second, archives);
            Assert.Equal("existing archive", File.ReadAllText(existingArchive));
            Assert.Equal("user file", File.ReadAllText(unrelatedFile));
        });
    }

    [Fact]
    public void Build_WhenPackingFails_LeavesNoTemporaryOrPartialArchive()
    {
        RunInTempDirectory(directory =>
        {
            var outputDirectory = Path.Combine(directory, "saved-files");
            var logPath = Path.Combine(directory, DiagnosticLogFiles.CurrentLogName);
            File.WriteAllText(logPath, "log");

            using var lockedLog = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => DiagnosticArchiveBuilder.Build(directory, outputDirectory, "summary", Stamp));

            Assert.Empty(Directory.GetFiles(outputDirectory));
        });
    }

    [Fact]
    public void Build_TruncatesAnOversizedLogToHeadAndTailAndSaysSo()
    {
        RunInTempDirectory(directory =>
        {
            var headLine = "FIRST LINE: STARTUP OK\r\n";
            var text = new StringBuilder();
            text.Append(headLine);
            // A real log is one record per line; a single 16 MB line would be pathological by
            // construction and is exactly what the redaction timeout exists to bound.
            var chunk = new string('x', 64 * 1024) + "\r\n";
            while (text.Length < DiagnosticArchiveBuilder.MaxLogBytes + 128 * 1024)
            {
                text.Append(chunk);
            }

            text.Append("FINAL LINE: CRASH HERE\r\n");
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), text.ToString());

            using var archive = ZipFile.OpenRead(Build(directory, "summary\r\n", Stamp));

            var packed = ReadEntry(archive, DiagnosticLogFiles.CurrentLogName);
            Assert.StartsWith("FIRST LINE: STARTUP OK\r\n", packed, StringComparison.Ordinal);
            Assert.EndsWith("FINAL LINE: CRASH HERE\r\n", packed, StringComparison.Ordinal);
            Assert.Contains("[Diag] LOG TRUNCATED | dropped_middle_bytes=", packed, StringComparison.Ordinal);
            Assert.Contains("log_truncated=Current.log", ReadEntry(archive, DiagnosticArchiveBuilder.SummaryEntryName));
            Assert.Contains("dropped_middle_bytes=", ReadEntry(archive, DiagnosticArchiveBuilder.SummaryEntryName));
        });
    }

    // SP-0123 done-when 1: a log written before the sink redacted - an earlier version's session kept on
    // disk - loses its credentials on the way into the archive, and keeps what makes it diagnosable.
    [Fact]
    public void Build_RedactsCredentialsInEveryPackedLog()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(
                Path.Combine(directory, "Session-20260730-0100.log"),
                "2026-07-30T01:00:00Z [Diag] PLAYER OPEN | url=rtsp://user:pass@host/x | backend=libvlc\r\n" +
                "2026-07-30T01:00:01Z [Error] open: failed 'https://host/s?token=abc&id=1'\r\n");
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "clean");

            using var archive = ZipFile.OpenRead(Build(directory, "summary", Stamp));

            var packed = string.Concat(archive.Entries.Select(entry => ReadEntry(archive, entry.FullName)));
            Assert.DoesNotContain("user:pass", packed, StringComparison.Ordinal);
            Assert.DoesNotContain("abc", packed, StringComparison.Ordinal);
            Assert.Contains("rtsp://host/x | backend=libvlc", packed, StringComparison.Ordinal);
            Assert.Contains("id=1", packed, StringComparison.Ordinal);
        });
    }

    // SP-0137 done-when 1: a log holding a profile path and a data-directory path - written by a version
    // whose sink did not yet redact them - reaches the archive without the account name or either raw root,
    // and with the path tail that makes it a diagnosis.
    [Fact]
    public void Build_ReplacesProfileAndDataDirectoryRootsInEveryPackedLog()
    {
        RunInTempDirectory(directory =>
        {
            const string account = "Zebulon Quarry";
            var profile = $@"C:\Users\{account}";
            var appData = $@"{profile}\AppData\Local\StreamsPlayer";
            File.WriteAllText(
                Path.Combine(directory, "Session-20260730-0100.log"),
                $@"2026-07-30T01:00:00Z [Diag] RECORD SAVED | file={profile}\Music\StreamsPlayer\rec.mp3 | ok=true" + "\r\n" +
                $@"2026-07-30T01:00:01Z [Error] save state: IOException: '{appData}\catalog-state.json' is locked" + "\r\n");
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "clean");

            var paths = new DiagnosticPathRedactor(appData, profile);
            using var archive = ZipFile.OpenRead(
                DiagnosticArchiveBuilder.Build(directory, Path.Combine(directory, "saved-files"), "summary", Stamp, paths));

            var packed = string.Concat(archive.Entries.Select(entry => ReadEntry(archive, entry.FullName)));
            Assert.DoesNotContain("Zebulon", packed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\Users", packed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"AppData\Local\StreamsPlayer", packed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"file=<USER>\Music\StreamsPlayer\rec.mp3 | ok=true", packed, StringComparison.Ordinal);
            Assert.Contains(@"'<APP_DATA>\catalog-state.json' is locked", packed, StringComparison.Ordinal);
        });
    }

    // The default redactor is the running user's: the real profile and the state directory, as the app calls it.
    [Fact]
    public void Build_ByDefaultReplacesTheRunningUsersProfileAndTheStateDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile));
        var account = Path.GetFileName(profile.TrimEnd('\\', '/'));

        RunInTempDirectory(directory =>
        {
            File.WriteAllText(
                Path.Combine(directory, DiagnosticLogFiles.CurrentLogName),
                $@"[Diag] A | file={profile}\Videos\x.mp4" + "\r\n" +
                $@"[Diag] B | log={Path.Combine(directory, "Session-1.log")}" + "\r\n");

            using var archive = ZipFile.OpenRead(Build(directory, "summary", Stamp));

            var packed = ReadEntry(archive, DiagnosticLogFiles.CurrentLogName);
            Assert.DoesNotContain(profile, packed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory, packed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\" + account + @"\", packed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"file=<USER>\Videos\x.mp4", packed, StringComparison.Ordinal);
            Assert.Contains(@"log=<APP_DATA>\Session-1.log", packed, StringComparison.Ordinal);
        });
    }

    // A truncation cut that lands inside a URL must not hand the redactor half an address it cannot read.
    [Fact]
    public void Build_TruncationCutsOnLineBoundariesSoASplitUrlCannotLeak()
    {
        RunInTempDirectory(directory =>
        {
            var line = "[Diag] PLAYER OPEN | url=rtsp://admin:hunter2@camera.local/stream1 | n=";
            var text = new StringBuilder();
            for (var index = 0; text.Length < DiagnosticArchiveBuilder.MaxLogBytes + 256 * 1024; index++)
            {
                text.Append(line).Append(index).Append("\r\n");
            }

            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), text.ToString());

            using var archive = ZipFile.OpenRead(Build(directory, "summary", Stamp));

            var packed = ReadEntry(archive, DiagnosticLogFiles.CurrentLogName);
            Assert.Contains("LOG TRUNCATED", packed, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", packed, StringComparison.Ordinal);
            Assert.DoesNotContain("admin", packed, StringComparison.Ordinal);
            Assert.All(
                packed.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Where(entry => !entry.Contains("LOG TRUNCATED")),
                entry => Assert.StartsWith("[Diag] PLAYER OPEN | url=rtsp://camera.local/stream1 | n=", entry, StringComparison.Ordinal));
        });
    }

    // SP-0174 (S44-02): the mail body names the archive by file name and folder alias. A draft can be
    // stored, quoted or forwarded, so the default download folder's path must not reach it.
    [Fact]
    public void DescribePathForMail_AnArchiveUnderTheUserProfileCarriesNoProfileFolder()
    {
        const string account = "Zebulon Quarry";
        var profile = $@"C:\Users\{account}";
        var archivePath = $@"{profile}\Downloads\StreamsPlayer-logs-20260929-101500.zip";

        var mailPath = DiagnosticArchiveBuilder.DescribePathForMail(archivePath, new DiagnosticPathRedactor(null, profile));

        Assert.Equal(@"<USER>\Downloads\StreamsPlayer-logs-20260929-101500.zip", mailPath);
        Assert.DoesNotContain("Users", mailPath, StringComparison.Ordinal);
        Assert.DoesNotContain(account, mailPath, StringComparison.OrdinalIgnoreCase);
    }

    // SP-0174 run-and-observe's Core half (acceptance 5): a session log that captured an IPTV address
    // with its password in the path - the ordinary shape an imported playlist produces - packs into an
    // archive that holds no copy of the password, in any entry, in any form.
    [Fact]
    public void Build_AnIptvPathPasswordReachesNoArchiveEntry()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(
                Path.Combine(directory, DiagnosticLogFiles.CurrentLogName),
                "2026-09-29T10:15:00Z [Diag] PLAYER OPEN | url=http://panel.example:8080/live/KioskUser/Hunter2Pass/12345.ts | backend=libvlc\r\n" +
                "2026-09-29T10:15:01Z [Error] open: System.Exception: failed 'http://panel.example:8080/movie/KioskUser/Hunter2Pass/9987.mkv?token=Tokn9'\r\n");

            using var archive = ZipFile.OpenRead(Build(directory, "summary", Stamp));

            var packed = string.Concat(archive.Entries.Select(entry => ReadEntry(archive, entry.FullName)));
            Assert.DoesNotContain("Hunter2Pass", packed, StringComparison.Ordinal);
            Assert.DoesNotContain("KioskUser", packed, StringComparison.Ordinal);
            Assert.DoesNotContain("Tokn9", packed, StringComparison.Ordinal);
            Assert.Contains("url=http://panel.example:8080/live/[REDACTED]/[REDACTED]/12345.ts", packed, StringComparison.Ordinal);
        });
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Build(string stateDirectory, string summary, DateTimeOffset stamp) =>
        DiagnosticArchiveBuilder.Build(stateDirectory, Path.Combine(stateDirectory, "saved-files"), summary, stamp);

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
