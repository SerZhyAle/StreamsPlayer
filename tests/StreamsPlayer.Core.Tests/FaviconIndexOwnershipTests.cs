using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0125, STREAM-BANK rule 6 / item A: a favicon index is an offset into the sheet of its own bank. When
/// that sheet is replaced, every index the new bank did not write has to go, or the row shows whichever
/// channel now occupies its old position - a confidently wrong icon, which SP-0088 ranks worse than none.
/// </summary>
public sealed class FaviconIndexOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Atlas = [1, 2, 3];
    private const string Gone = "https://example.test/gone";
    private const string Listed = "https://example.test/listed";

    [Fact]
    public void Refresh_ClearsTheIndexOfARowItRetiresWhileReplacingTheSheet()
    {
        var pinned = Channel(Gone) with { Pinned = true, FaviconIndex = 7 };

        var result = Refresh(new CatalogState { Channels = [pinned] }, Atlas);

        var retired = Assert.Single(result.State.Channels, channel => channel.Id == pinned.Id);
        Assert.NotNull(retired.RetiredAt);
        Assert.Null(retired.FaviconIndex);
        Assert.Equal(3, Assert.Single(result.State.Channels, channel => channel.Url == Listed).FaviconIndex);
    }

    [Fact]
    public void Refresh_WithoutASheetKeepsTheIndexOfARetiredRow()
    {
        // The stored sheet stays when the bank arrives without one, so the old index still resolves
        // against the sheet it was written for.
        var pinned = Channel(Gone) with { Pinned = true, FaviconIndex = 7 };

        var result = Refresh(new CatalogState { Channels = [pinned], AtlasFileName = "favicon-atlas-old.png" }, atlas: null);

        Assert.Equal(7, Assert.Single(result.State.Channels, channel => channel.Id == pinned.Id).FaviconIndex);
        Assert.Equal("favicon-atlas-old.png", result.State.AtlasFileName);
    }

    [Fact]
    public void Refresh_ClearsACatalogIndexOnARowTheUserOwns()
    {
        // The row an edit turned Manual before SP-0125 still carries the bank's index; a merge never
        // rewrites its metadata, but the index would point into a sheet that is about to be deleted.
        var edited = Channel(Listed) with { SourceOrigin = SourceOrigin.Manual, FaviconIndex = 4, Title = "Mine" };

        var result = Refresh(new CatalogState { Channels = [edited] }, Atlas);

        var row = Assert.Single(result.State.Channels);
        Assert.Equal("Mine", row.Title);
        Assert.Equal(SourceOrigin.Manual, row.SourceOrigin);
        Assert.Null(row.FaviconIndex);
    }

    [Fact]
    public void Refresh_LeavesIndicesIntoOtherSlotsAlone()
    {
        var snapshotRow = Channel(Gone) with { Pinned = true, FaviconIndex = 2, FaviconSource = FaviconSource.Snapshot };

        var result = Refresh(new CatalogState { Channels = [snapshotRow], SnapshotAtlasFileName = "snapshot-atlas-a.png" }, Atlas);

        Assert.Equal(2, Assert.Single(result.State.Channels, channel => channel.Id == snapshotRow.Id).FaviconIndex);
        Assert.Equal("snapshot-atlas-a.png", result.State.SnapshotAtlasFileName);
    }

    [Fact]
    public void ImportedSlot_ReplacementClearsIndicesTheNewImportDidNotWrite()
    {
        var earlier = Channel(Gone) with { SourceOrigin = SourceOrigin.LocalCatalog, FaviconIndex = 9, FaviconSource = FaviconSource.Imported };

        var result = CatalogMerger.Merge(
            [earlier],
            [Entry(Listed, 1)],
            Now,
            new CatalogMergeOptions(
                RemoveMissing: false,
                FaviconSource: FaviconSource.Imported,
                TargetOrigin: SourceOrigin.LocalCatalog,
                ReplacesAtlas: true));

        Assert.Null(Assert.Single(result.Channels, channel => channel.Id == earlier.Id).FaviconIndex);
        Assert.Equal(1, Assert.Single(result.Channels, channel => channel.Url == Listed).FaviconIndex);
    }

    [Fact]
    public void SnapshotSlot_ReplacementClearsIndicesTheNewSnapshotDidNotWrite()
    {
        var earlier = Channel(Gone) with { FaviconIndex = 9, FaviconSource = FaviconSource.Snapshot };
        var outcome = new CatalogSnapshotOutcome(new CatalogSnapshot(
            new StreamBank([Entry(Listed, 1)], Atlas, CsvWasFirstEntry: true, MaximumFaviconIndex: 1),
            Now,
            StreamCatalogService.CatalogUrl));

        var result = outcome.Apply(new CatalogState { Channels = [earlier], SnapshotAtlasFileName = "snapshot-atlas-a.png" });

        Assert.Null(Assert.Single(result.State.Channels, channel => channel.Id == earlier.Id).FaviconIndex);
        Assert.Equal(1, Assert.Single(result.State.Channels, channel => channel.Url == Listed).FaviconIndex);
    }

    [Fact]
    public async Task Load_GivesARowWithAnUnreadableSourceNoIndex()
    {
        var directory = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "catalog-state.json"),
                """
                {"channels":[
                  {"id":"6f1d6a64-1f9a-4d3f-9a55-3d7f2c1b0a01","url":"https://example.test/a","title":"A",
                   "mediaKind":"Audio","sourceOrigin":"Catalog","addedAt":"2026-09-25T12:00:00+00:00",
                   "faviconIndex":5,"faviconSource":"SomeFutureSheet"},
                  {"id":"6f1d6a64-1f9a-4d3f-9a55-3d7f2c1b0a02","url":"https://example.test/b","title":"B",
                   "mediaKind":"Audio","sourceOrigin":"Catalog","addedAt":"2026-09-25T12:00:00+00:00",
                   "faviconIndex":6,"faviconSource":"Snapshot"}
                ]}
                """);

            var state = await new StreamCatalogStore(directory).LoadAsync();

            var unreadable = Assert.Single(state.Channels, channel => channel.Url.EndsWith("/a", StringComparison.Ordinal));
            Assert.Null(unreadable.FaviconIndex);
            Assert.Equal(FaviconSource.Catalog, unreadable.FaviconSource);
            var readable = Assert.Single(state.Channels, channel => channel.Url.EndsWith("/b", StringComparison.Ordinal));
            Assert.Equal(6, readable.FaviconIndex);
            Assert.Equal(FaviconSource.Snapshot, readable.FaviconSource);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Refresh_ThatAbsorbsEveryImportedRowReleasesTheImportedSheet()
    {
        var directory = TemporaryDirectory();
        try
        {
            var store = new StreamCatalogStore(directory);
            var imported = Channel(Listed) with
            {
                SourceOrigin = SourceOrigin.LocalCatalog,
                FaviconIndex = 5,
                FaviconSource = FaviconSource.Imported
            };
            var seeded = await store.SaveAsync(new CatalogState { Channels = [imported] }, [9, 9], replaceAtlas: true, AtlasSlot.Imported);
            var importedSheet = store.ResolveAtlasPath(seeded, AtlasSlot.Imported)!;
            Assert.True(File.Exists(importedSheet));

            var result = Refresh(seeded, Atlas);
            var saved = await store.SaveAsync(result.State, Atlas, replaceAtlas: true);

            var row = Assert.Single(saved.Channels);
            Assert.Equal(SourceOrigin.Catalog, row.SourceOrigin);
            Assert.Equal(FaviconSource.Catalog, row.FaviconSource);
            Assert.Null(saved.ImportedAtlasFileName);
            Assert.False(File.Exists(importedSheet));
            Assert.True(File.Exists(store.ResolveAtlasPath(saved)!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReleaseUnreferenced_KeepsOnlySlotsSomeIndexPointsInto()
    {
        var state = new CatalogState
        {
            Channels =
            [
                Channel(Listed) with { FaviconIndex = 1 },
                Channel(Gone) with { FaviconIndex = null, FaviconSource = FaviconSource.Snapshot }
            ],
            AtlasFileName = "favicon-atlas-a.png",
            SnapshotAtlasFileName = "snapshot-atlas-a.png",
            ImportedAtlasFileName = "imported-atlas-a.png"
        };

        var released = FaviconAtlasReferences.ReleaseUnreferenced(state, AtlasSlot.Imported);

        Assert.Equal("favicon-atlas-a.png", released.AtlasFileName);
        Assert.Null(released.SnapshotAtlasFileName);
        // The operation's own slot is its save's to decide (SP-0088 keeps it on a degraded bank).
        Assert.Equal("imported-atlas-a.png", released.ImportedAtlasFileName);
    }

    private static CatalogRefreshResult Refresh(CatalogState state, byte[]? atlas) =>
        new CatalogRefreshOutcome(
                new StreamBank([Entry(Listed, 3)], atlas, CsvWasFirstEntry: true, MaximumFaviconIndex: 3),
                Now)
            .Apply(state);

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "sp0125-" + Guid.NewGuid().ToString("N"));

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Title",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = Now
    };

    private static CatalogEntry Entry(string url, int? faviconIndex) =>
        new("Title", url, MediaKind.Audio, null, null, null, null, null, faviconIndex);
}
