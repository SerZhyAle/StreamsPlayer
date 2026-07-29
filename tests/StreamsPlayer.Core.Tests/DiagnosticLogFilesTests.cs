using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0040 phase 01: the retention rule that makes a crash report possible after a restart.
/// </summary>
public sealed class DiagnosticLogFilesTests
{
    [Fact]
    public void Rotate_WithNoCurrentLog_LeavesTheDirectoryUntouched()
    {
        RunInTempDirectory(directory =>
        {
            DiagnosticLogFiles.RotateCurrentToPrevious(directory);

            Assert.Empty(Directory.GetFiles(directory));
        });
    }

    [Fact]
    public void Rotate_MovesTheCurrentSessionLogOntoThePreviousName()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "first session");

            DiagnosticLogFiles.RotateCurrentToPrevious(directory);

            Assert.False(File.Exists(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName)));
            Assert.Equal("first session", File.ReadAllText(Path.Combine(directory, DiagnosticLogFiles.PreviousLogName)));
        });
    }

    // Two generations, not a growing history: the second rotation must replace, not accumulate.
    [Fact]
    public void Rotate_Twice_KeepsOnlyTheMostRecentPreviousSession()
    {
        RunInTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "session one");
            DiagnosticLogFiles.RotateCurrentToPrevious(directory);
            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "session two");
            DiagnosticLogFiles.RotateCurrentToPrevious(directory);

            Assert.Equal("session two", File.ReadAllText(Path.Combine(directory, DiagnosticLogFiles.PreviousLogName)));
            Assert.Single(Directory.GetFiles(directory));
        });
    }

    [Fact]
    public void ExistingLogs_ReturnsCurrentThenPreviousAndSkipsAbsentFiles()
    {
        RunInTempDirectory(directory =>
        {
            Assert.Empty(DiagnosticLogFiles.ExistingLogs(directory));

            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.PreviousLogName), "old");
            Assert.Equal(
                [Path.Combine(directory, DiagnosticLogFiles.PreviousLogName)],
                DiagnosticLogFiles.ExistingLogs(directory));

            File.WriteAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName), "new");
            Assert.Equal(
                [
                    Path.Combine(directory, DiagnosticLogFiles.CurrentLogName),
                    Path.Combine(directory, DiagnosticLogFiles.PreviousLogName)
                ],
                DiagnosticLogFiles.ExistingLogs(directory));
        });
    }

    // The log facade calls this before it has proven the directory exists; a launch must survive it.
    [Fact]
    public void Rotate_OnAMissingDirectory_DoesNotThrow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");

        DiagnosticLogFiles.RotateCurrentToPrevious(directory);

        Assert.False(Directory.Exists(directory));
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
