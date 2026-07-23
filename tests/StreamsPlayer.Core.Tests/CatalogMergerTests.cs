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
