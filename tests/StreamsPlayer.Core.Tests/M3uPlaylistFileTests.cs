using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0177: the file side of M3U import and export - bounded like the URL side, and atomic.</summary>
public sealed class M3uPlaylistFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sp-m3u-" + Guid.NewGuid().ToString("N"));

    public M3uPlaylistFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Write_ThenRead_RoundTripsThePlaylist()
    {
        var path = Path.Combine(_directory, "list.m3u");

        await M3uPlaylistFile.WriteAsync(path, [Channel("Radio", "https://example.test/radio")]);
        var entry = Assert.Single(M3uPlaylistParser.Parse(await M3uPlaylistFile.ReadAsync(path)));

        Assert.Equal("https://example.test/radio", entry.Url);
        Assert.Equal("Radio", entry.Title);
    }

    [Fact]
    public async Task Write_ThatFailsMidway_LeavesThePreviousFileIntact()
    {
        var path = Path.Combine(_directory, "list.m3u");
        await File.WriteAllTextAsync(path, "#EXTM3U\nprevious\n");

        await Assert.ThrowsAsync<IOException>(() => M3uPlaylistFile.WriteAtomicAsync(
            path,
            async (stream, token) =>
            {
                await stream.WriteAsync("#EXTM3U\npart"u8.ToArray(), token);
                throw new IOException("disk full");
            },
            CancellationToken.None));

        Assert.Equal("#EXTM3U\nprevious\n", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Read_RefusesAFileOverTheUrlCeiling()
    {
        var path = Path.Combine(_directory, "video.mp4");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(M3uImportService.MaximumPlaylistBytes + 1);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => M3uPlaylistFile.ReadAsync(path));
    }

    private static StreamChannel Channel(string title, string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = title,
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Manual,
        AddedAt = DateTimeOffset.UnixEpoch
    };
}
