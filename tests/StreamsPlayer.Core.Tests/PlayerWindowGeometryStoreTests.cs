using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0175: the file half of SP-0084. A failed read must never turn into a save over the file it failed
/// to read - one closed window must not replace every remembered placement with a single entry - and a
/// document that parses but holds null entries must load as empty rather than throw in placement.
/// </summary>
public sealed class PlayerWindowGeometryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sp0175-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task AnAbsentFile_LoadsAsNothingAndSaves()
    {
        var store = new PlayerWindowGeometryStore(_directory);
        var at = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var entries = new[] { new ChannelWindowGeometry("https://host/live.m3u8", at, 10, 20, 640, 360) };

        Assert.Empty(await store.LoadAsync());
        Assert.True(await store.SaveAsync(entries));

        var loaded = await store.LoadAsync();
        Assert.Equal("https://host/live.m3u8", Assert.Single(loaded).Url);
    }

    [Fact]
    public async Task AMalformedFile_RefusesTheNextSaveAndStaysByteIdentical()
    {
        var store = new PlayerWindowGeometryStore(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(store.FilePath, "{ this is not the list it used to be");
        var before = await File.ReadAllBytesAsync(store.FilePath);

        Assert.Empty(await store.LoadAsync());
        Assert.False(await store.SaveAsync(
            [new ChannelWindowGeometry("https://host/live.m3u8", DateTimeOffset.UtcNow, 10, 20, 640, 360)]));

        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath));
    }

    [Fact]
    public async Task ALockedFile_RefusesTheNextSaveAndStaysByteIdentical()
    {
        var store = new PlayerWindowGeometryStore(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(store.FilePath, "[]");
        using (File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(await store.LoadAsync());
            Assert.False(await store.SaveAsync(
                [new ChannelWindowGeometry("https://host/live.m3u8", DateTimeOffset.UtcNow, 10, 20, 640, 360)]));
        }

        Assert.Equal("[]", await File.ReadAllTextAsync(store.FilePath));
    }

    // SP-0175: an empty file holds nothing to preserve, so it counts as absent and a save may create it.
    [Fact]
    public async Task AZeroByteFile_CountsAsAbsentAndMayBeSavedOver()
    {
        var store = new PlayerWindowGeometryStore(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(store.FilePath, []);

        Assert.Empty(await store.LoadAsync());
        Assert.True(await store.SaveAsync(
            [new ChannelWindowGeometry("https://host/live.m3u8", DateTimeOffset.UtcNow, 10, 20, 640, 360)]));
    }

    // SP-0184: a read that failed at startup must not strand the session - the next successful load leaves the
    // unreadable state, and the merge keeps both the file's placements and the ones recorded meanwhile.
    [Fact]
    public async Task AFileThatBecomesReadable_ClearsTheRefusalAndMergesWithTheSession()
    {
        var store = new PlayerWindowGeometryStore(_directory);
        var at = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(_directory);
        Assert.True(await store.SaveAsync(
        [
            new ChannelWindowGeometry("https://host/a.m3u8", at, 1, 2, 640, 360),
            new ChannelWindowGeometry("https://host/b.m3u8", at, 3, 4, 640, 360)
        ]));
        IReadOnlyList<ChannelWindowGeometry> session =
            [new ChannelWindowGeometry("https://host/b.m3u8", at.AddHours(1), 9, 9, 800, 450)];

        using (File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await store.LoadAsync();
            Assert.True(store.IsUnreadable);
        }

        var stored = await store.LoadAsync();
        Assert.False(store.IsUnreadable);

        var merged = PlayerWindowGeometry.Merge(stored, session);
        Assert.Equal(2, merged.Count);
        Assert.Equal(9, merged.Single(entry => entry.Url.EndsWith("b.m3u8", StringComparison.Ordinal)).Left);
        Assert.True(await store.SaveAsync(merged));
    }

    // SP-0175: null entries used to pass the deserialization catch and then throw in Recall and Record.
    [Fact]
    public async Task ADocumentOfNullEntries_LoadsAsEmptyWithoutThrowing()
    {
        var store = new PlayerWindowGeometryStore(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(store.FilePath, "[null]");

        var loaded = await store.LoadAsync();
        Assert.Empty(loaded);
        Assert.Null(PlayerWindowGeometry.Recall(loaded, "https://host/live.m3u8"));
    }
}
