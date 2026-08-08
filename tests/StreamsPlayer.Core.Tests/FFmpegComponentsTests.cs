using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class FFmpegComponentsTests
{
    [Fact]
    public void MissingLibraries_ReportsEveryLibraryWhenTheFolderIsAbsent()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");

        Assert.Equal(FFmpegComponents.RequiredLibraries, FFmpegComponents.MissingLibraries(folder));
        Assert.False(FFmpegComponents.IsInstalled(folder));
    }

    [Fact]
    public void MissingLibraries_NamesTheOneLibraryThatIsAbsent()
    {
        WithFolder(folder =>
        {
            var absent = FFmpegComponents.RequiredLibraries[3];
            WriteAll(folder, except: absent);

            Assert.Equal([absent], FFmpegComponents.MissingLibraries(folder));
            Assert.False(FFmpegComponents.IsInstalled(folder));
        });
    }

    [Fact]
    public void MissingLibraries_TreatsAZeroLengthFileAsAbsent()
    {
        WithFolder(folder =>
        {
            WriteAll(folder);
            var truncated = FFmpegComponents.RequiredLibraries[0];
            File.WriteAllBytes(Path.Combine(folder, truncated), []);

            Assert.Equal([truncated], FFmpegComponents.MissingLibraries(folder));
        });
    }

    [Fact]
    public void IsInstalled_IsTrueOnlyWithTheCompleteSet()
    {
        WithFolder(folder =>
        {
            WriteAll(folder);

            Assert.True(FFmpegComponents.IsInstalled(folder));
            Assert.Empty(FFmpegComponents.MissingLibraries(folder));
        });
    }

    [Fact]
    public void Remove_DeletesTheLibrariesAndThenTheEmptyFolder()
    {
        WithFolder(folder =>
        {
            WriteAll(folder);

            FFmpegComponents.Remove(folder);

            Assert.False(Directory.Exists(folder));
        });
    }

    [Fact]
    public void Remove_KeepsFilesItDoesNotOwn()
    {
        WithFolder(folder =>
        {
            WriteAll(folder);
            var foreign = Path.Combine(folder, "LICENSE.txt");
            File.WriteAllText(foreign, "kept");

            FFmpegComponents.Remove(folder);

            Assert.True(File.Exists(foreign));
            Assert.False(FFmpegComponents.IsInstalled(folder));
            Assert.Equal([foreign], Directory.GetFileSystemEntries(folder));
        });
    }

    [Fact]
    public void Remove_IsSilentOnAFolderThatWasNeverInstalled()
    {
        FFmpegComponents.Remove(Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}"));
    }

    [Fact]
    public void ResolveFolder_SitsInsideTheDataDirectory()
    {
        Assert.Equal(
            Path.Combine("C:\\data", FFmpegComponents.FolderName),
            FFmpegComponents.ResolveFolder("C:\\data"));
    }

    internal static void WriteAll(string folder, string? except = null)
    {
        Directory.CreateDirectory(folder);
        foreach (var library in FFmpegComponents.RequiredLibraries)
        {
            if (library != except)
            {
                File.WriteAllBytes(Path.Combine(folder, library), [0x4d, 0x5a]);
            }
        }
    }

    private static void WithFolder(Action<string> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            body(folder);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
