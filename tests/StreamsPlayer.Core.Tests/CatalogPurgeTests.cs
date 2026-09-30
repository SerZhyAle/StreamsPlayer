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

        Assert.Equal([manual, imported, localCatalog], result.State.Channels);
        Assert.Equal([catalogOne.Id, catalogTwo.Id], result.RemovedChannelIds);
        Assert.True(result.State.Channels.Single(channel => channel.Id == manual.Id).Pinned);
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
        StreamChannel[] channels =
        [
            Channel("https://example.test/one", SourceOrigin.Catalog),
            Channel("https://example.test/two", SourceOrigin.Catalog),
            Channel("rtsp://example.test/camera", SourceOrigin.Manual),
            Channel("https://example.test/imported", SourceOrigin.Imported),
            Channel("https://example.test/local-catalog", SourceOrigin.LocalCatalog)
        ];

        Assert.Equal(2, CatalogPurge.CountDownloaded(channels));
        Assert.Equal(0, CatalogPurge.CountDownloaded([]));
    }

    [Fact]
    public void RemoveImportedBank_DropsLocalCatalogRowsAndCleansImportedAtlas()
    {
        var localOne = Channel("https://example.test/local-one", SourceOrigin.LocalCatalog);
        var localTwo = Channel("https://example.test/local-two", SourceOrigin.LocalCatalog) with { Pinned = true };
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
        StreamChannel[] channels =
        [
            Channel("https://example.test/one", SourceOrigin.Catalog),
            Channel("https://example.test/two", SourceOrigin.LocalCatalog),
            Channel("https://example.test/three", SourceOrigin.LocalCatalog),
            Channel("rtsp://example.test/camera", SourceOrigin.Manual),
            Channel("https://example.test/imported", SourceOrigin.Imported)
        ];

        Assert.Equal(2, CatalogPurge.CountImportedBank(channels));
        Assert.Equal(0, CatalogPurge.CountImportedBank([]));
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
