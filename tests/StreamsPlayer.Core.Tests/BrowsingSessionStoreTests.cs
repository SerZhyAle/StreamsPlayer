using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0067: the browsing session is the one thing that moved out of the catalog file, so what a user
/// upgrading across this change keeps - and what the two stores do to each other's files - is a
/// contract rather than an implementation detail.
/// </summary>
public sealed class BrowsingSessionStoreTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");

    private static void Cleanup(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CatalogState PopulatedState() => new()
    {
        CatalogSearchQuery = "jazz",
        CatalogMediaFilter = "Audio",
        CatalogCategoryFilter = "Music",
        CatalogTopicFilter = "News",
        CatalogLanguageFilter = "Ukrainian",
        CatalogCountryFilter = "UA",
        CatalogMinBitrateFilter = "128",
        CatalogCollectionFilter = "8f2b9c17-0000-0000-0000-000000000001",
        CatalogSortMode = "Country",
        CatalogScrollAnchorId = Guid.NewGuid(),
        LastSelectedChannelId = Guid.NewGuid()
    };

    [Fact]
    public async Task SaveThenLoad_ReturnsEveryField()
    {
        var directory = NewDirectory();
        try
        {
            var store = new BrowsingSessionStore(directory);
            var session = new BrowsingSession
            {
                SearchQuery = "classical",
                MediaFilter = "Video",
                CategoryFilter = "Talk",
                TopicFilter = "Sport",
                LanguageFilter = "German",
                CountryFilter = "DE",
                MinBitrateFilter = "256",
                CollectionFilter = "favourites",
                SortMode = "Recently added",
                ScrollOffset = 4821.5,
                LastSelectedChannelId = Guid.NewGuid()
            };

            await store.SaveAsync(session);
            var loaded = await store.LoadAsync(new CatalogState());

            Assert.Equal(session, loaded);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Load_WithNoSessionFile_MigratesFromCatalogStateAndWritesTheFile()
    {
        var directory = NewDirectory();
        try
        {
            var store = new BrowsingSessionStore(directory);
            var state = PopulatedState();

            Assert.False(File.Exists(store.SessionPath));
            var migrated = await store.LoadAsync(state);

            Assert.Equal(state.CatalogSearchQuery, migrated.SearchQuery);
            Assert.Equal(state.CatalogMediaFilter, migrated.MediaFilter);
            Assert.Equal(state.CatalogCategoryFilter, migrated.CategoryFilter);
            Assert.Equal(state.CatalogTopicFilter, migrated.TopicFilter);
            Assert.Equal(state.CatalogLanguageFilter, migrated.LanguageFilter);
            Assert.Equal(state.CatalogCountryFilter, migrated.CountryFilter);
            Assert.Equal(state.CatalogMinBitrateFilter, migrated.MinBitrateFilter);
            Assert.Equal(state.CatalogCollectionFilter, migrated.CollectionFilter);
            Assert.Equal(state.CatalogSortMode, migrated.SortMode);
            Assert.Equal(state.LastSelectedChannelId, migrated.LastSelectedChannelId);

            // The anchor named a channel; the session stores a position. Restoring the top of the list is
            // deliberate - inventing an offset would put the user somewhere they never were.
            Assert.Equal(0, migrated.ScrollOffset);
            Assert.True(File.Exists(store.SessionPath));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Load_MigratesOnlyOnce()
    {
        var directory = NewDirectory();
        try
        {
            var store = new BrowsingSessionStore(directory);
            var first = await store.LoadAsync(PopulatedState());
            Assert.Equal("jazz", first.SearchQuery);

            // The old fields keep changing (an older build still writing them, a downgrade and back);
            // after the one-time migration none of it may reach the session again.
            var second = await store.LoadAsync(new CatalogState
            {
                CatalogSearchQuery = "changed-afterwards",
                CatalogSortMode = "Language"
            });

            Assert.Equal("jazz", second.SearchQuery);
            Assert.Equal("Country", second.SortMode);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Load_WithCorruptFile_ReturnsDefaultsAndDoesNotThrow()
    {
        var directory = NewDirectory();
        try
        {
            var store = new BrowsingSessionStore(directory);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(store.SessionPath, "{ this is not json");

            var loaded = await store.LoadAsync(PopulatedState());

            // Defaults, not the migration: the file exists, so the old fields are deliberately not read.
            // Losing the filters is recoverable; failing the launch over them is not.
            Assert.Equal(new BrowsingSession(), loaded);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // The two stores share a directory and each sweeps stranded temp files. A prefix collision would
    // have one delete the other's file mid-write, which is why the session prefix is not "catalog-state-".
    [Fact]
    public async Task TheTwoStoresDoNotSweepEachOthersFiles()
    {
        var directory = NewDirectory();
        try
        {
            var catalogStore = new StreamCatalogStore(directory);
            var sessionStore = new BrowsingSessionStore(directory);

            await catalogStore.SaveAsync(new CatalogState { CatalogSearchQuery = "kept" });
            await sessionStore.SaveAsync(new BrowsingSession { SearchQuery = "kept" });
            Assert.True(File.Exists(catalogStore.StatePath));
            Assert.True(File.Exists(sessionStore.SessionPath));

            await sessionStore.SaveAsync(new BrowsingSession { SearchQuery = "again" });
            Assert.True(File.Exists(catalogStore.StatePath));

            await catalogStore.SaveAsync(new CatalogState(), [1, 2, 3], replaceAtlas: true);
            Assert.True(File.Exists(sessionStore.SessionPath));
            Assert.Equal("again", (await sessionStore.LoadAsync(new CatalogState())).SearchQuery);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // SP-0175: a session file that could not be read is never written over - the scroll saves that run
    // several times a minute used to replace it with the fresh session, costless only until the folder
    // lock cleared and the user's filters were gone. The store now refuses the save, the file stays
    // byte-identical, and the load still answers a usable fresh session.
    [Fact]
    public async Task Load_WithALockedFile_ReturnsFreshRefusesSavesAndKeepsTheFile()
    {
        var directory = NewDirectory();
        try
        {
            var store = new BrowsingSessionStore(directory);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(store.SessionPath, """{"searchQuery":"kept"}""");

            BrowsingSession loaded;
            using (File.Open(store.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                loaded = await store.LoadAsync(PopulatedState());
                await Assert.ThrowsAsync<IOException>(
                    () => store.SaveAsync(new BrowsingSession { SearchQuery = "refused" }));
            }

            Assert.Equal(new BrowsingSession(), loaded);
            Assert.Equal("""{"searchQuery":"kept"}""", await File.ReadAllTextAsync(store.SessionPath));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // SP-0175 / SP-0116 A-18: the migration save used to sit outside the load's guard, so a locked
    // state folder failed the whole start as a catalog load failure. It now defers to the next launch -
    // the session file is still absent, so the migration simply runs again - and the migrated session
    // is returned regardless.
    [Fact]
    public async Task Load_WhenTheMigrationSaveFails_StillReturnsTheMigratedSessionAndReports()
    {
        // A file where the data directory should be: every attempt to create the directory or write
        // the session file fails with IOException, which is exactly a locked state folder.
        var directory = NewDirectory();
        await File.WriteAllTextAsync(directory, "occupied");
        try
        {
            var store = new BrowsingSessionStore(directory);
            Exception? reported = null;

            var migrated = await store.LoadAsync(PopulatedState(), onMigrationSaveFailure: e => reported = e);

            Assert.Equal("jazz", migrated.SearchQuery);
            Assert.Equal("Country", migrated.SortMode);
            Assert.IsType<IOException>(reported);
        }
        finally
        {
            Cleanup(directory);
        }
    }
}
