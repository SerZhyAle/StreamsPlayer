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
        var state = new CatalogState { Channels = [catalogOne, manual, catalogTwo, imported] };

        var result = CatalogPurge.RemoveDownloaded(state);

        Assert.Equal([manual, imported], result.State.Channels);
        Assert.Equal([catalogOne.Id, catalogTwo.Id], result.RemovedChannelIds);
        Assert.True(result.State.Channels.Single(channel => channel.Id == manual.Id).Pinned);
    }

    [Fact]
    public void RemoveDownloaded_LeavesTheRestOfTheStateUntouched()
    {
        var state = new CatalogState
        {
            Channels = [Channel("https://example.test/catalog", SourceOrigin.Catalog)],
            // Decisions 4 and 5: hide choices, the atlas, and the recorded download time survive a purge.
            HiddenCatalogUrls = ["https://example.test/hidden"],
            AtlasFileName = "favicon-atlas.png",
            LastCatalogRefreshAt = Now,
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
        Assert.Equal(state.AtlasFileName, result.State.AtlasFileName);
        Assert.Equal(state.LastCatalogRefreshAt, result.State.LastCatalogRefreshAt);
        Assert.Equal(state.ListeningHistory, result.State.ListeningHistory);
        Assert.Equal(state.TileSize, result.State.TileSize);
        Assert.Equal(state.Language, result.State.Language);
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
            Channel("https://example.test/imported", SourceOrigin.Imported)
        ];

        Assert.Equal(2, CatalogPurge.CountDownloaded(channels));
        Assert.Equal(0, CatalogPurge.CountDownloaded([]));
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
