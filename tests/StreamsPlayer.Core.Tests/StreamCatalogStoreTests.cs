using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamCatalogStoreTests
{
    [Fact]
    public async Task Save_ReplacesStateAndAtlasReferenceTogether()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var first = await store.SaveAsync(new CatalogState(), [1, 2, 3], replaceAtlas: true);
            var second = await store.SaveAsync(first with { LastCatalogRefreshAt = DateTimeOffset.UtcNow }, [4, 5], replaceAtlas: true);
            var loaded = await store.LoadAsync();

            Assert.Equal(second.AtlasFileName, loaded.AtlasFileName);
            Assert.Equal(second.LastCatalogRefreshAt, loaded.LastCatalogRefreshAt);
            Assert.Equal(second.Channels, loaded.Channels);
            Assert.NotEqual(first.AtlasFileName, second.AtlasFileName);
            Assert.Equal([4, 5], await File.ReadAllBytesAsync(store.ResolveAtlasPath(loaded)!));
            Assert.False(File.Exists(store.ResolveAtlasPath(first)!));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_PreservesGridViewPreference()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);

            await store.SaveAsync(new CatalogState { ViewMode = CatalogViewMode.Grid });

            var loaded = await store.LoadAsync();
            Assert.Equal(CatalogViewMode.Grid, loaded.ViewMode);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_PreservesLanguageAndWindowPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState
            {
                Language = AppLanguage.Russian,
                MainWindowTopmost = true,
                PlayerWindowTopmost = true,
                VideoVolume = 35,
                VideoMuted = true
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(AppLanguage.Russian, loaded.Language);
            Assert.True(loaded.MainWindowTopmost);
            Assert.True(loaded.PlayerWindowTopmost);
            Assert.Equal(35, loaded.VideoVolume);
            Assert.True(loaded.VideoMuted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_PreservesGridSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            Assert.Equal(StreamTileSize.Medium, new CatalogState().TileSize);
            Assert.True(new CatalogState().UpdateStreamPreviews);

            await store.SaveAsync(new CatalogState
            {
                TileSize = StreamTileSize.Large,
                UpdateStreamPreviews = false
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(StreamTileSize.Large, loaded.TileSize);
            Assert.False(loaded.UpdateStreamPreviews);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_PreservesLastSelectedChannelId()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var id = Guid.NewGuid();

            await store.SaveAsync(new CatalogState { LastSelectedChannelId = id });

            var loaded = await store.LoadAsync();
            Assert.Equal(id, loaded.LastSelectedChannelId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_PreservesCatalogBrowsingSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var anchorId = Guid.NewGuid();
            await store.SaveAsync(new CatalogState
            {
                CatalogSearchQuery = "jazz",
                CatalogMediaFilter = "Audio",
                CatalogCategoryFilter = "Music",
                CatalogLanguageFilter = "english",
                CatalogCountryFilter = "US",
                CatalogSortMode = "Recently added",
                CatalogScrollAnchorId = anchorId
            });

            var loaded = await store.LoadAsync();

            Assert.Equal("jazz", loaded.CatalogSearchQuery);
            Assert.Equal("Audio", loaded.CatalogMediaFilter);
            Assert.Equal("Music", loaded.CatalogCategoryFilter);
            Assert.Equal("english", loaded.CatalogLanguageFilter);
            Assert.Equal("US", loaded.CatalogCountryFilter);
            Assert.Equal("Recently added", loaded.CatalogSortMode);
            Assert.Equal(anchorId, loaded.CatalogScrollAnchorId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
