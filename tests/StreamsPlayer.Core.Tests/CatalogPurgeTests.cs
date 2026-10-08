using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogPurgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RemoveDownloaded_DropsCatalogRowsAndKeepsUserRows()
    {
        var catalogOne = Channel("https://example.test/catalog-one", SourceOrigin.Catalog);
        var catalogTwo = Channel("https://example.test/catalog-two", SourceOrigin.Catalog) with { Pinned = true };
        var manual = Channel("rtsp://example.test/camera", SourceOrigin.Manual) with { Pinned = true };
        var imported = Channel("https://example.test/imported", SourceOrigin.Imported);
        var localCatalog = Channel("https://example.test/local-catalog", SourceOrigin.LocalCatalog);
        var state = new CatalogState { Channels = [catalogOne, manual, catalogTwo, imported, localCatalog] };

        var result = CatalogPurge.RemoveDownloaded(state);

        // SP-0183: pinned catalog channel (catalogTwo) should be preserved
        Assert.Equal(4, result.State.Channels.Count);
        Assert.Contains(manual, result.State.Channels);
        Assert.Contains(imported, result.State.Channels);
        Assert.Contains(localCatalog, result.State.Channels);
        Assert.Contains(catalogTwo, result.State.Channels);
        Assert.Equal([catalogOne.Id], result.RemovedChannelIds);
        Assert.True(result.State.Channels.Single(channel => channel.Id == manual.Id).Pinned);
        Assert.True(result.State.Channels.Single(channel => channel.Id == catalogTwo.Id).Pinned);
    }

    [Fact]
    public void RemoveDownloaded_LeavesTheRestOfTheStateUntouched()
    {
        var state = new CatalogState
        {
            Channels = [Channel("https://example.test/catalog", SourceOrigin.Catalog)],
            // Decision 4: hide choices survive a purge. SP-0177 superseded decision 5: the atlas and the
            // download time describe the removed rows, so they leave with them (the test below).
            HiddenCatalogUrls = ["https://example.test/hidden"],
            ListeningHistory = [new ListeningHistoryEntry
            {
                ChannelId = Guid.NewGuid(),
                Title = "Played",
                MediaKind = MediaKind.Audio,
                LastPlayedAt = Now
            }],
            TileSize = StreamTileSize.Large,
            Language = AppLanguage.Russian
        };

        var result = CatalogPurge.RemoveDownloaded(state);

        Assert.Empty(result.State.Channels);
        Assert.Equal(state.HiddenCatalogUrls, result.State.HiddenCatalogUrls);
        Assert.Equal(state.ListeningHistory, result.State.ListeningHistory);
        Assert.Equal(state.TileSize, result.State.TileSize);
        Assert.Equal(state.Language, result.State.Language);
    }

    // SP-0177: kept names kept the atlas files, the provenance line kept claiming a refresh, and the
    // first-run snapshot offer could never qualify again.
    [Fact]
    public void RemoveDownloaded_ReleasesAtlasesAndProvenanceButKeepsAnAtlasStillInUse()
    {
        var imported = Channel("https://example.test/local", SourceOrigin.LocalCatalog) with
        {
            FaviconSource = FaviconSource.Imported,
            FaviconIndex = 0
        };
        var state = new CatalogState
        {
            Channels =
            [
                Channel("https://example.test/catalog", SourceOrigin.Catalog) with { FaviconIndex = 1 },
                Channel("https://example.test/seeded", SourceOrigin.Catalog) with { FaviconSource = FaviconSource.Snapshot, FaviconIndex = 2 },
                Channel("https://example.test/mine", SourceOrigin.Manual),
                imported
            ],
            AtlasFileName = "favicon-atlas-a.png",
            SnapshotAtlasFileName = "favicon-atlas-b.png",
            ImportedAtlasFileName = "favicon-atlas-c.png",
            LastCatalogRefreshAt = Now,
            AppliedSnapshotDate = Now.AddDays(-30)
        };

        var result = CatalogPurge.RemoveDownloaded(state).State;

        Assert.Equal(2, result.Channels.Count);
        Assert.Null(result.AtlasFileName);
        Assert.Null(result.SnapshotAtlasFileName);
        Assert.Equal("favicon-atlas-c.png", result.ImportedAtlasFileName);
        Assert.Null(result.LastCatalogRefreshAt);
        Assert.Null(result.AppliedSnapshotDate);
    }

    [Fact]
    public void RemoveDownloaded_WithoutCatalogRowsIsANoOp()
    {
        var state = new CatalogState { Channels = [Channel("rtsp://example.test/camera", SourceOrigin.Manual)] };

        var result = CatalogPurge.RemoveDownloaded(state);

        Assert.Same(state, result.State);
        Assert.Empty(result.RemovedChannelIds);
    }

    [Fact]
    public void CountDownloaded_CountsOnlyCatalogRows()
    {
        // SP-0183: CountDownloaded now takes a CatalogState and excludes user-authored channels
        var channels = new List<StreamChannel>
        {
            Channel("https://example.test/one", SourceOrigin.Catalog),
            Channel("https://example.test/two", SourceOrigin.Catalog),
            Channel("rtsp://example.test/camera", SourceOrigin.Manual),
            Channel("https://example.test/imported", SourceOrigin.Imported),
            Channel("https://example.test/local-catalog", SourceOrigin.LocalCatalog)
        };
        
        var state = new CatalogState { Channels = channels };
        
        // Without any user-authored content, both catalog channels should be counted
        Assert.Equal(2, CatalogPurge.CountDownloaded(state));
        
        // Test with empty state
        var emptyState = new CatalogState { Channels = [] };
        Assert.Equal(0, CatalogPurge.CountDownloaded(emptyState));
    }

    [Fact]
    public void CountDownloaded_ExcludesUserAuthoredChannels()
    {
        // SP-0183: user-authored channels (pinned) should not be counted for removal
        var pinnedChannel = Channel("https://example.test/pinned", SourceOrigin.Catalog) with { Pinned = true };
        var catalogChannel = Channel("https://example.test/regular", SourceOrigin.Catalog);
        
        var channels = new List<StreamChannel> { pinnedChannel, catalogChannel };
        var state = new CatalogState { Channels = channels };
        
        // Only the non-pinned catalog channel should be counted
        Assert.Equal(1, CatalogPurge.CountDownloaded(state));
    }

    [Fact]
    public void RemoveImportedBank_DropsLocalCatalogRowsAndCleansImportedAtlas()
    {
        var localOne = Channel("https://example.test/local-one", SourceOrigin.LocalCatalog);
        var localTwo = Channel("https://example.test/local-two", SourceOrigin.LocalCatalog);
        var catalog = Channel("https://example.test/catalog", SourceOrigin.Catalog);
        var manual = Channel("rtsp://example.test/camera", SourceOrigin.Manual) with { Pinned = true };
        var imported = Channel("https://example.test/imported", SourceOrigin.Imported);
        var state = new CatalogState
        {
            Channels = [localOne, catalog, manual, localTwo, imported],
            ImportedAtlasFileName = "imported-atlas-123.png",
            AtlasFileName = "favicon-atlas.png"
        };

        var result = CatalogPurge.RemoveImportedBank(state);

        Assert.Equal([catalog, manual, imported], result.State.Channels);
        Assert.Equal([localOne.Id, localTwo.Id], result.RemovedChannelIds);
        Assert.Null(result.State.ImportedAtlasFileName);
        Assert.Equal("favicon-atlas.png", result.State.AtlasFileName);
    }

    // SP-0184 A14-1: pinned, collected and listened-to imported rows are the user's work and survive the purge.
    [Fact]
    public void RemoveImportedBank_KeepsPinnedCollectedAndListenedRows()
    {
        var plain = Channel("https://example.test/plain", SourceOrigin.LocalCatalog);
        var pinned = Channel("https://example.test/pinned", SourceOrigin.LocalCatalog) with { Pinned = true };
        var collected = Channel("https://example.test/collected", SourceOrigin.LocalCatalog);
        var listened = Channel("https://example.test/listened", SourceOrigin.LocalCatalog);
        var state = new CatalogState
        {
            Channels = [plain, pinned, collected, listened],
            Collections = [new ChannelCollection { Id = Guid.NewGuid(), Name = "Mine", ChannelIds = [collected.Id] }],
            ListeningHistory = [new ListeningHistoryEntry
            {
                ChannelId = listened.Id,
                Title = "Played",
                MediaKind = MediaKind.Audio,
                LastPlayedAt = Now
            }]
        };

        Assert.Equal(1, CatalogPurge.CountImportedBank(state));

        var result = CatalogPurge.RemoveImportedBank(state);

        Assert.Equal([plain.Id], result.RemovedChannelIds);
        Assert.Equal([pinned, collected, listened], result.State.Channels);
    }

    [Fact]
    public void RemoveImportedBank_KeepsTheImportedAtlasWhileAKeptRowStillIndexesIt()
    {
        var keptRow = Channel("https://example.test/kept", SourceOrigin.LocalCatalog) with
        {
            Pinned = true,
            FaviconSource = FaviconSource.Imported,
            FaviconIndex = 0
        };
        var state = new CatalogState
        {
            Channels = [keptRow, Channel("https://example.test/gone", SourceOrigin.LocalCatalog)],
            ImportedAtlasFileName = "imported-atlas-123.png"
        };

        var result = CatalogPurge.RemoveImportedBank(state);

        Assert.Equal([keptRow], result.State.Channels);
        Assert.Equal("imported-atlas-123.png", result.State.ImportedAtlasFileName);
    }

    [Fact]
    public void RemoveImportedBank_WithOnlyAuthoredRowsIsANoOp()
    {
        var state = new CatalogState
        {
            Channels = [Channel("https://example.test/pinned", SourceOrigin.LocalCatalog) with { Pinned = true }],
            ImportedAtlasFileName = "imported-atlas-123.png"
        };

        var result = CatalogPurge.RemoveImportedBank(state);

        Assert.Same(state, result.State);
        Assert.Empty(result.RemovedChannelIds);
        Assert.Equal(0, CatalogPurge.CountImportedBank(state));
    }

    [Fact]
    public void RemoveImportedBank_WithoutLocalCatalogRowsIsANoOp()
    {
        var state = new CatalogState
        {
            Channels = [Channel("https://example.test/catalog", SourceOrigin.Catalog)],
            AtlasFileName = "favicon-atlas.png"
        };

        var result = CatalogPurge.RemoveImportedBank(state);

        Assert.Same(state, result.State);
        Assert.Empty(result.RemovedChannelIds);
    }

    [Fact]
    public void CountImportedBank_CountsOnlyLocalCatalogRows()
    {
        var state = new CatalogState
        {
            Channels =
            [
                Channel("https://example.test/one", SourceOrigin.Catalog),
                Channel("https://example.test/two", SourceOrigin.LocalCatalog),
                Channel("https://example.test/three", SourceOrigin.LocalCatalog),
                Channel("rtsp://example.test/camera", SourceOrigin.Manual),
                Channel("https://example.test/imported", SourceOrigin.Imported)
            ]
        };

        Assert.Equal(2, CatalogPurge.CountImportedBank(state));
        Assert.Equal(0, CatalogPurge.CountImportedBank(new CatalogState { Channels = [] }));
    }

    private static StreamChannel Channel(string url, SourceOrigin origin) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Title",
        MediaKind = MediaKind.Audio,
        SourceOrigin = origin,
        AddedAt = Now,
        SortIndex = 3
    };
}
