using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogMergerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Merge_UpdatesCatalogMetadataButPreservesLocalState()
    {
        var original = Channel("https://example.test/one", SourceOrigin.Catalog) with
        {
            Title = "Old",
            Pinned = true,
            SortIndex = -4,
            LastPlayOutcome = PlayOutcome.Ok
        };
        var entry = Entry("New", original.Url, MediaKind.Video) with
        {
            Protocol = "HLS",
            Format = "AAC",
            Bitrate = "128 kbps",
            IsLive = true
        };

        var result = CatalogMerger.Merge([original], [entry], Now);
        var merged = Assert.Single(result.Channels);

        Assert.Equal(original.Id, merged.Id);
        Assert.Equal("New", merged.Title);
        Assert.True(merged.Pinned);
        Assert.Equal(-4, merged.SortIndex);
        Assert.Equal(PlayOutcome.Ok, merged.LastPlayOutcome);
        // SP-0018: refreshed technical metadata rides along; user/ordering state is preserved.
        Assert.Equal("HLS", merged.Protocol);
        Assert.Equal("AAC", merged.Format);
        Assert.Equal("128 kbps", merged.Bitrate);
        Assert.True(merged.IsLive);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void Merge_UserRowWinsCollisionAndSurvivesPrune()
    {
        var manual = Channel("https://example.test/same", SourceOrigin.Manual);
        var staleCatalog = Channel("https://example.test/stale", SourceOrigin.Catalog);

        var result = CatalogMerger.Merge(
            [manual, staleCatalog],
            [Entry("Catalog title", manual.Url, MediaKind.Video)],
            Now);

        var survivor = Assert.Single(result.Channels);
        Assert.Equal(manual, survivor);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Added);
    }

    [Fact]
    public void Merge_SameCatalogTwiceIsNoOp()
    {
        var entry = Entry("One", "https://example.test/one", MediaKind.Audio);
        var first = CatalogMerger.Merge([], [entry], Now);
        var second = CatalogMerger.Merge(first.Channels, [entry], Now.AddMinutes(1));

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Removed);
    }

    [Fact]
    public void Merge_CarriesAccessOntoAddedAndUpdatedCatalogRows()
    {
        var entry = Entry("Geo", "https://example.test/geo", MediaKind.Video) with
        {
            Access = ChannelAccess.GeoRestricted
        };

        var added = Assert.Single(CatalogMerger.Merge([], [entry], Now).Channels);
        Assert.Equal(ChannelAccess.GeoRestricted, added.Access);

        // A later catalog that drops the tag must clear it again, not leave a stale warning behind.
        var cleared = Assert.Single(CatalogMerger.Merge(
            [added],
            [entry with { Access = ChannelAccess.Open }],
            Now.AddMinutes(1)).Channels);
        Assert.Equal(ChannelAccess.Open, cleared.Access);
    }

    [Fact]
    public void Merge_UserRowNeverTakesAccessFromTheCatalog()
    {
        var manual = Channel("https://example.test/same", SourceOrigin.Manual);

        var result = CatalogMerger.Merge(
            [manual],
            [Entry("Catalog title", manual.Url, MediaKind.Video) with { Access = ChannelAccess.GeoRestricted }],
            Now);

        Assert.Equal(ChannelAccess.Open, Assert.Single(result.Channels).Access);
        Assert.Equal(0, result.Updated);
    }

    // SP-0052 AC 5: applying the bundled snapshot adds and updates but never prunes, and it must protect
    // user rows exactly as an online refresh does.
    [Fact]
    public void Merge_WithoutRemovalKeepsCatalogRowsTheEntriesDoNotMention()
    {
        var survivor = Channel("https://example.test/downloaded", SourceOrigin.Catalog);
        var manual = Channel("https://example.test/manual", SourceOrigin.Manual);
        var imported = Channel("https://example.test/imported", SourceOrigin.Imported);

        var result = CatalogMerger.Merge(
            [survivor, manual, imported],
            [Entry("Snapshot only", "https://example.test/snapshot", MediaKind.Audio)],
            Now,
            new CatalogMergeOptions(RemoveMissing: false, FaviconSource: FaviconSource.Snapshot));

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Added);
        Assert.Equal(4, result.Channels.Count);
        Assert.Contains(result.Channels, channel => channel == manual);
        Assert.Contains(result.Channels, channel => channel == imported);
        Assert.Equal(FaviconSource.Catalog, Assert.Single(result.Channels, c => c.Id == survivor.Id).FaviconSource);
    }

    // SP-0052 AC 7: a favicon index and the atlas it indexes are one pair, so the source is stamped on
    // rows the snapshot adds and on rows it updates - including one a download had brought in.
    [Fact]
    public void Merge_StampsTheFaviconSourceOnAddedAndUpdatedRows()
    {
        var entry = Entry("One", "https://example.test/one", MediaKind.Audio);
        var snapshotOptions = new CatalogMergeOptions(RemoveMissing: false, FaviconSource: FaviconSource.Snapshot);

        var added = Assert.Single(CatalogMerger.Merge([], [entry], Now, snapshotOptions).Channels);
        Assert.Equal(FaviconSource.Snapshot, added.FaviconSource);

        var downloaded = Assert.Single(CatalogMerger.Merge([], [entry with { Title = "Downloaded" }], Now).Channels);
        Assert.Equal(FaviconSource.Catalog, downloaded.FaviconSource);

        var restamped = Assert.Single(CatalogMerger.Merge([downloaded], [entry], Now, snapshotOptions).Channels);
        Assert.Equal(downloaded.Id, restamped.Id);
        Assert.Equal(FaviconSource.Snapshot, restamped.FaviconSource);
    }

    // SP-0052 AC 6: a later online refresh treats snapshot rows as its own - updating, pruning and
    // returning them to the downloaded atlas, with no duplicate left behind.
    [Fact]
    public void Merge_OnlineRefreshReclaimsAndPrunesSnapshotRows()
    {
        var snapshotOptions = new CatalogMergeOptions(RemoveMissing: false, FaviconSource: FaviconSource.Snapshot);
        var kept = Entry("Kept", "https://example.test/kept", MediaKind.Audio);
        var dropped = Entry("Dropped", "https://example.test/dropped", MediaKind.Audio);
        var seeded = CatalogMerger.Merge([], [kept, dropped], Now, snapshotOptions);

        var refreshed = CatalogMerger.Merge(seeded.Channels, [kept with { Title = "Kept, renamed" }], Now.AddDays(1));

        var survivor = Assert.Single(refreshed.Channels);
        Assert.Equal("Kept, renamed", survivor.Title);
        Assert.Equal(FaviconSource.Catalog, survivor.FaviconSource);
        Assert.Equal(1, refreshed.Removed);
        Assert.Equal(0, refreshed.Added);
        Assert.Distinct(refreshed.Channels.Select(channel => channel.Url));
    }

    // SP-0098: Local catalog import merges channels as SourceOrigin.LocalCatalog
    [Fact]
    public void Merge_LocalCatalogImport_StampsLocalCatalogOrigin()
    {
        var entry = Entry("Local Channel", "https://example.test/local", MediaKind.Video);
        var options = new CatalogMergeOptions(
            RemoveMissing: false,
            FaviconSource: FaviconSource.Imported,
            TargetOrigin: SourceOrigin.LocalCatalog);

        var result = CatalogMerger.Merge([], [entry], Now, options);

        var channel = Assert.Single(result.Channels);
        Assert.Equal(SourceOrigin.LocalCatalog, channel.SourceOrigin);
        Assert.Equal(FaviconSource.Imported, channel.FaviconSource);
        Assert.Equal(1, result.Added);
    }

    // SP-0098: Local catalog import does not overwrite or duplicate existing SourceOrigin.Catalog rows
    [Fact]
    public void Merge_LocalCatalogImport_PreservesExistingCatalogRowOnCollision()
    {
        var existingCatalog = Channel("https://example.test/shared", SourceOrigin.Catalog) with
        {
            Title = "Published Catalog Station"
        };
        var incomingLocal = Entry("Local Bank Station", existingCatalog.Url, MediaKind.Video);
        var options = new CatalogMergeOptions(
            RemoveMissing: false,
            FaviconSource: FaviconSource.Imported,
            TargetOrigin: SourceOrigin.LocalCatalog);

        var result = CatalogMerger.Merge([existingCatalog], [incomingLocal], Now, options);

        var channel = Assert.Single(result.Channels);
        Assert.Equal(existingCatalog.Id, channel.Id);
        Assert.Equal("Published Catalog Station", channel.Title);
        Assert.Equal(SourceOrigin.Catalog, channel.SourceOrigin);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
    }

    // SP-0098: Network refresh never prunes LocalCatalog rows
    [Fact]
    public void Merge_OnlineRefresh_NeverPrunesLocalCatalogRows()
    {
        var localChannel = Channel("https://example.test/local-exclusive", SourceOrigin.LocalCatalog) with
        {
            Title = "Local Exclusive"
        };
        var publishedChannel = Channel("https://example.test/published", SourceOrigin.Catalog) with
        {
            Title = "Published"
        };
        var networkEntry = Entry("Published Updated", publishedChannel.Url, MediaKind.Video);

        var result = CatalogMerger.Merge([localChannel, publishedChannel], [networkEntry], Now);

        Assert.Equal(2, result.Channels.Count);
        Assert.Contains(result.Channels, c => c.Id == localChannel.Id && c.SourceOrigin == SourceOrigin.LocalCatalog);
        var updatedPublished = Assert.Single(result.Channels, c => c.Id == publishedChannel.Id);
        Assert.Equal("Published Updated", updatedPublished.Title);
        Assert.Equal(0, result.Removed);
    }

    // SP-0098: Network refresh updates colliding LocalCatalog rows and promotes them to Catalog
    [Fact]
    public void Merge_OnlineRefresh_PromotesCollidingLocalCatalogRowToCatalog()
    {
        var localChannel = Channel("https://example.test/promoted", SourceOrigin.LocalCatalog) with
        {
            Title = "Local Old Title",
            Pinned = true
        };
        var networkEntry = Entry("Published New Title", localChannel.Url, MediaKind.Video);

        var result = CatalogMerger.Merge([localChannel], [networkEntry], Now);

        var channel = Assert.Single(result.Channels);
        Assert.Equal(localChannel.Id, channel.Id);
        Assert.Equal("Published New Title", channel.Title);
        Assert.Equal(SourceOrigin.Catalog, channel.SourceOrigin);
        Assert.True(channel.Pinned);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
    }

    // SP-0098: URLs differing only by casing/normalization are treated as collisions
    [Fact]
    public void Merge_NormalizesUrlsForCollisionDetection()
    {
        var existing = Channel("https://example.test:443/Stream", SourceOrigin.Catalog);
        var incoming = Entry("Case Normalized", "HTTPS://EXAMPLE.TEST/Stream", MediaKind.Audio);

        var result = CatalogMerger.Merge([existing], [incoming], Now);

        var channel = Assert.Single(result.Channels);
        Assert.Equal(existing.Id, channel.Id);
        Assert.Equal("Case Normalized", channel.Title);
    }

    // SP-0177: a user row sorting first used to hide its catalog twin from every later merge.
    [Fact]
    public void Merge_UpdatesTheCatalogTwinWhenAUserTwinComesFirst()
    {
        var user = Channel("https://Example.test:443/twin", SourceOrigin.Manual) with { Title = "Mine" };
        var catalog = Channel("https://example.test/twin", SourceOrigin.Catalog) with { RetiredAt = Now };

        var result = CatalogMerger.Merge([user, catalog], [Entry("Bank title", "https://example.test/twin", MediaKind.Audio)], Now);

        Assert.Equal(user, Assert.Single(result.Channels, channel => channel.Id == user.Id));
        var updated = Assert.Single(result.Channels, channel => channel.Id == catalog.Id);
        Assert.Equal("Bank title", updated.Title);
        Assert.Null(updated.RetiredAt);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);
    }

    // SP-0177: a snapshot older than the last download adds what is missing and leaves downloaded rows alone.
    [Fact]
    public void Merge_PreservingLiveRowsSkipsDownloadedRowsOnly()
    {
        var downloaded = Channel("https://example.test/live", SourceOrigin.Catalog) with { Title = "Newer" };
        var seeded = Channel("https://example.test/seeded", SourceOrigin.Catalog) with { FaviconSource = FaviconSource.Snapshot };
        var options = new CatalogMergeOptions(
            RemoveMissing: false, FaviconSource: FaviconSource.Snapshot, RevivesRetired: false, PreservesLiveDownloadedRows: true);

        var result = CatalogMerger.Merge(
            [downloaded, seeded],
            [
                Entry("Older", "https://example.test/live", MediaKind.Audio),
                Entry("Seed update", "https://example.test/seeded", MediaKind.Audio),
                Entry("New", "https://example.test/new", MediaKind.Audio)
            ],
            Now,
            options);

        Assert.Equal(downloaded, Assert.Single(result.Channels, channel => channel.Id == downloaded.Id));
        Assert.Equal("Seed update", Assert.Single(result.Channels, channel => channel.Id == seeded.Id).Title);
        Assert.Contains(result.Channels, channel => channel.Title == "New");
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

    private static CatalogEntry Entry(string title, string url, MediaKind kind) =>
        new(title, url, kind, "News", "World", "english", "MT", null, 2);
}
