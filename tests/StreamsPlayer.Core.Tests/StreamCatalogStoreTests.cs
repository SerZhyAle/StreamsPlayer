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

    // SP-0033 AC 7: the tag round-trips, and a state file written before the property existed must load
    // as Open with no migration step.
    [Fact]
    public async Task Save_RoundTripsChannelAccessAndDefaultsLegacyStateToOpen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var channel = new StreamChannel
            {
                Id = Guid.NewGuid(),
                Url = "https://example.test/geo",
                Title = "Geo",
                MediaKind = MediaKind.Video,
                SourceOrigin = SourceOrigin.Catalog,
                AddedAt = DateTimeOffset.UtcNow,
                Access = ChannelAccess.GeoRestricted
            };

            await store.SaveAsync(new CatalogState { Channels = [channel] });
            Assert.Equal(ChannelAccess.GeoRestricted, Assert.Single((await store.LoadAsync()).Channels).Access);

            var legacy = """
            {
              "channels": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "url": "https://example.test/legacy",
                  "title": "Legacy",
                  "mediaKind": "Audio",
                  "sourceOrigin": "Catalog",
                  "addedAt": "2026-01-01T00:00:00+00:00"
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(directory, "catalog-state.json"), legacy);

            Assert.Equal(ChannelAccess.Open, Assert.Single((await store.LoadAsync()).Channels).Access);
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
                Theme = AppTheme.Dark,
                MainWindowTopmost = true,
                PlayerWindowTopmost = true,
                VideoVolume = 35,
                VideoMuted = true
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(AppLanguage.Russian, loaded.Language);
            Assert.Equal(AppTheme.Dark, loaded.Theme);
            Assert.True(loaded.MainWindowTopmost);
            Assert.True(loaded.PlayerWindowTopmost);
            Assert.Equal(35, loaded.VideoVolume);
            Assert.True(loaded.VideoMuted);
            // Never seeded is the default for a state file written before SP-0031.
            Assert.Null(loaded.ChannelPreviewAtlasRevision);
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
                TileSize = StreamTileSize.VerySmall,
                UpdateStreamPreviews = false
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(StreamTileSize.VerySmall, loaded.TileSize);
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

    [Fact]
    public async Task Save_PreservesChannelPreviewAtlasRevision()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState
            {
                ChannelPreviewAtlasRevision = ChannelPreviewAtlasService.Revision
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(ChannelPreviewAtlasService.Revision, loaded.ChannelPreviewAtlasRevision);
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
    public async Task Save_FailedCommitLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            // A directory where the state file belongs makes the commit move fail deterministically.
            Directory.CreateDirectory(Path.Combine(directory, "catalog-state.json"));
            var store = new StreamCatalogStore(directory);

            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(new CatalogState()));

            Assert.Empty(Directory.GetFiles(directory, "catalog-state-*.tmp"));
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
    public async Task Save_SweepsStrandedTemporaryFilesButKeepsRecentOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var stranded = Path.Combine(directory, $"catalog-state-{Guid.NewGuid():N}.tmp");
            var recent = Path.Combine(directory, $"catalog-state-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(stranded, "{}");
            await File.WriteAllTextAsync(recent, "{}");
            File.SetLastWriteTimeUtc(stranded, DateTime.UtcNow.AddHours(-2));

            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState());

            Assert.False(File.Exists(stranded));
            // A temp file young enough to belong to a concurrent save (another instance) is never touched.
            Assert.True(File.Exists(recent));
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
    public async Task Save_ConcurrentSavesCommitWithoutStrandingTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var saves = Enumerable.Range(0, 16)
                .Select(index => store.SaveAsync(new CatalogState { AudioVolume = index }))
                .ToArray();

            await Task.WhenAll(saves);

            Assert.Empty(Directory.GetFiles(directory, "catalog-state-*.tmp"));
            var loaded = await store.LoadAsync();
            Assert.Contains(loaded.AudioVolume, Enumerable.Range(0, 16));
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
