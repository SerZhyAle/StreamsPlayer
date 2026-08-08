using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0055: the retention rule. The point of keeping ten launches is that the evidence a user wants to
/// mail survives the restarts they make while the player is misbehaving, so these tests are mostly about
/// what must <em>not</em> disappear.
/// </summary>
public sealed class DiagnosticLogFilesTests
{
    // Carried over from SP-0040: the first launch on a clean profile has nothing to retire.
    [Fact]
    public void Rotate_WithNoCurrentLog_LeavesTheDirectoryUntouched()
    {
        RunInTempDirectory(directory =>
        {
            DiagnosticLogFiles.Rotate(directory);

            Assert.Empty(Directory.GetFiles(directory));
        });
    }

    // Carried over from SP-0040: the log facade calls this before it has proven the directory exists -
    // and now Rotate also enumerates that directory to prune, so both halves must tolerate its absence.
    [Fact]
    public void Rotate_OnAMissingDirectory_DoesNotThrow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");

        DiagnosticLogFiles.Rotate(directory);

        Assert.False(Directory.Exists(directory));
    }

    // Upgrading from an SP-0040 build: the spare log that scheme wrote must join the new naming rather
    // than sit there forever, unpackable and unprunable - it may hold the very session being reported.
    [Fact]
    public void Rotate_AdoptsTheLegacyPreviousLogOnTheFirstLaunchAfterUpgrade()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "Previous.log"), "session from the old build");
            WriteLog(directory, DiagnosticLogFiles.CurrentLogName, "2026-08-07T14:36:05.0000000+00:00 [Information] startup");

            DiagnosticLogFiles.Rotate(directory);

            Assert.False(File.Exists(Path.Combine(directory, "Previous.log")));
            var retired = Directory.GetFiles(directory, "Session-*.log");
            Assert.Equal(2, retired.Length);
            Assert.Contains(retired, path => File.ReadAllText(path) == "session from the old build");
            Assert.Contains(DiagnosticLogFiles.ExistingLogs(directory), path => File.ReadAllText(path) == "session from the old build");
        });
    }

    [Fact]
    public void Rotate_RetiresTheCurrentLogUnderTheSessionStartFromItsFirstRecord()
    {
        RunInTempDirectory(directory =>
        {
            var started = new DateTimeOffset(2026, 8, 7, 14, 36, 5, TimeSpan.Zero);
            WriteLog(directory, DiagnosticLogFiles.CurrentLogName, $"{started:O} [Information] Application startup.");

            DiagnosticLogFiles.Rotate(directory);

            var expected = $"Session-{started.ToLocalTime():yyyyMMdd-HHmm}.log";
            Assert.False(File.Exists(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName)));
            Assert.True(File.Exists(Path.Combine(directory, expected)));
        });
    }

    // A log whose first line is not a timestamp - truncated by a hard power loss, or written by a build
    // that logged something else first - must still be retired rather than lost or left in place.
    [Fact]
    public void Rotate_WithAnUnparseableFirstLine_StillRetiresTheLog()
    {
        RunInTempDirectory(directory =>
        {
            WriteLog(directory, DiagnosticLogFiles.CurrentLogName, "\0\0 garbage");

            DiagnosticLogFiles.Rotate(directory);

            Assert.False(File.Exists(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName)));
            Assert.Single(Directory.GetFiles(directory, "Session-*.log"));
        });
    }

    // Two launches inside the same minute is what an automated run or an impatient restart looks like.
    // The second must not overwrite the first: that is precisely the evidence rotation exists to keep.
    [Fact]
    public void Rotate_TwiceWithinTheSameMinute_KeepsBothSessions()
    {
        RunInTempDirectory(directory =>
        {
            var started = new DateTimeOffset(2026, 8, 7, 14, 36, 5, TimeSpan.Zero);
            WriteLog(directory, DiagnosticLogFiles.CurrentLogName, $"{started:O} [Information] first");
            DiagnosticLogFiles.Rotate(directory);
            WriteLog(directory, DiagnosticLogFiles.CurrentLogName, $"{started:O} [Information] second");
            DiagnosticLogFiles.Rotate(directory);

            var retired = Directory.GetFiles(directory, "Session-*.log");
            Assert.Equal(2, retired.Length);
            Assert.Contains(retired, path => File.ReadAllText(path).Contains("first"));
            Assert.Contains(retired, path => File.ReadAllText(path).Contains("second"));
        });
    }

    // The promise is "the last ten launches are kept", counting the one running now. So the tenth
    // launch must still delete nothing, and the eleventh must delete exactly one - the oldest.
    [Fact]
    public void Rotate_OnTheTenthLaunch_DeletesNothing()
    {
        RunInTempDirectory(directory =>
        {
            RetireSessions(directory, count: DiagnosticLogFiles.KeptSessions - 2); // launches 1..8 closed
            WriteLog(directory, DiagnosticLogFiles.CurrentLogName, "2026-08-07T14:36:05.0000000+00:00 [Information] ninth");

            DiagnosticLogFiles.Rotate(directory); // tenth launch starts: the ninth is retired

            Assert.Equal(DiagnosticLogFiles.KeptSessions - 1, Directory.GetFiles(directory, "Session-*.log").Length);
        });
    }

    [Fact]
    public void Rotate_OnTheEleventhLaunch_DropsOnlyTheOldest()
    {
        RunInTempDirectory(directory =>
        {
            RetireSessions(directory, count: DiagnosticLogFiles.KeptSessions - 1); // launches 1..9 closed
            WriteLog(directory, DiagnosticLogFiles.CurrentLogName, "2026-08-07T14:36:05.0000000+00:00 [Information] tenth");

            DiagnosticLogFiles.Rotate(directory); // eleventh launch starts: the tenth is retired

            var retired = Directory.GetFiles(directory, "Session-*.log").Select(Path.GetFileName).ToList();
            Assert.Equal(DiagnosticLogFiles.KeptSessions - 1, retired.Count);
            Assert.DoesNotContain("Session-20260801-0100.log", retired); // the oldest, and only it
            Assert.Contains("Session-20260801-0200.log", retired);
        });
    }

    // The whole feature is retention, but a launch must survive a directory that refuses to cooperate -
    // the current log is the hard requirement, retention is the convenience.
    [Fact]
    public void Rotate_WithTheCurrentLogHeldOpen_DoesNotThrowAndStillPrunes()
    {
        RunInTempDirectory(directory =>
        {
            RetireSessions(directory, count: DiagnosticLogFiles.KeptSessions - 1);
            var stale = Path.Combine(directory, "Session-20260101-0000.log");
            File.WriteAllText(stale, "oldest");
            File.SetLastWriteTimeUtc(stale, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var current = Path.Combine(directory, DiagnosticLogFiles.CurrentLogName);
            File.WriteAllText(current, "locked");
            using var hold = new FileStream(current, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            DiagnosticLogFiles.Rotate(directory);

            Assert.True(File.Exists(current)); // could not be retired, and that is acceptable
            Assert.False(File.Exists(stale)); // pruning still ran
        });
    }

    [Fact]
    public void ExistingLogs_ListsTheCurrentSessionFirstThenTheRetiredNewestFirst()
    {
        RunInTempDirectory(directory =>
        {
            // Carried over from SP-0040: nothing to report, and a retired log with no live session -
            // which is what the archive builder sees if the current log could not be opened.
            Assert.Empty(DiagnosticLogFiles.ExistingLogs(directory));
            var orphan = Path.Combine(directory, "Session-20260801-0500.log");
            File.WriteAllText(orphan, "no live session");
            Assert.Equal([orphan], DiagnosticLogFiles.ExistingLogs(directory));
            File.Delete(orphan);

            var older = Path.Combine(directory, "Session-20260801-0100.log");
            var newer = Path.Combine(directory, "Session-20260801-0900.log");
            File.WriteAllText(older, "older");
            File.WriteAllText(newer, "newer");
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "current");

            var logs = DiagnosticLogFiles.ExistingLogs(directory).Select(Path.GetFileName).ToList();

            Assert.Equal([DiagnosticLogFiles.CurrentLogName, "Session-20260801-0900.log", "Session-20260801-0100.log"], logs);
        });
    }

    // catalog-state.json and the atlases live in this directory too; only session logs are ours to sweep.
    [Fact]
    public void Rotate_NeverTouchesFilesThatAreNotSessionLogs()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "catalog-state.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "favicon-atlas-abc.png"), "png");
            RetireSessions(directory, count: DiagnosticLogFiles.KeptSessions + 3);

            DiagnosticLogFiles.Rotate(directory);

            Assert.True(File.Exists(Path.Combine(directory, "catalog-state.json")));
            Assert.True(File.Exists(Path.Combine(directory, "favicon-atlas-abc.png")));
        });
    }

    /// <summary>Lays down <paramref name="count"/> already-retired session logs, oldest at 01:00.</summary>
    private static void RetireSessions(string directory, int count)
    {
        for (var index = 1; index <= count; index++)
        {
            var path = Path.Combine(directory, $"Session-20260801-{index:D2}00.log");
            File.WriteAllText(path, $"session {index}");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 1, index, 0, 0, DateTimeKind.Utc));
        }
    }

    private static void WriteLog(string directory, string name, string content) =>
        File.WriteAllText(Path.Combine(directory, name), content + Environment.NewLine);

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
