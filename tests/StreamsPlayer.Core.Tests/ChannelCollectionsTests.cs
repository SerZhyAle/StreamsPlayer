using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ChannelCollectionsTests
{
    private static readonly Guid NewsId = Guid.NewGuid();
    private static readonly Guid CamerasId = Guid.NewGuid();
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly Guid ChannelB = Guid.NewGuid();
    private static readonly Guid ChannelC = Guid.NewGuid();

    [Theory]
    [InlineData("  News  ", "News")]
    [InlineData("Morning\tradio", "Morning radio")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizeName_TrimsAndCollapses(string? input, string? expected)
    {
        Assert.Equal(expected, ChannelCollections.NormalizeName(input));
    }

    [Fact]
    public void NormalizeName_BoundsTheLength()
    {
        var name = ChannelCollections.NormalizeName(new string('x', ChannelCollections.MaximumNameLength + 20));

        Assert.Equal(ChannelCollections.MaximumNameLength, name!.Length);
    }

    [Fact]
    public void Create_RejectsBlankAndCaseInsensitiveDuplicates()
    {
        var collections = ChannelCollections.Create([], "News", NewsId)!;

        Assert.Single(collections);
        Assert.Null(ChannelCollections.Create(collections, "  news ", Guid.NewGuid()));
        Assert.Null(ChannelCollections.Create(collections, "   ", Guid.NewGuid()));
        Assert.NotNull(ChannelCollections.Create(collections, "Cameras", CamerasId));
    }

    [Fact]
    public void Rename_KeepsItsOwnNameButRejectsAnotherCollectionsName()
    {
        var collections = ChannelCollections.Create(
            ChannelCollections.Create([], "News", NewsId)!, "Cameras", CamerasId)!;

        Assert.NotNull(ChannelCollections.Rename(collections, NewsId, " news "));
        Assert.Null(ChannelCollections.Rename(collections, NewsId, "cameras"));
        Assert.Null(ChannelCollections.Rename(collections, Guid.NewGuid(), "Anything"));

        var renamed = ChannelCollections.Rename(collections, NewsId, "Headlines")!;
        Assert.Equal("Headlines", renamed.Single(collection => collection.Id == NewsId).Name);
    }

    [Fact]
    public void Membership_IsPerCollectionOrderedAndDeduplicated()
    {
        var collections = ChannelCollections.Create(
            ChannelCollections.Create([], "News", NewsId)!, "Cameras", CamerasId)!;

        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelA);
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelB);
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelA); // no-op
        collections = ChannelCollections.AddChannel(collections, CamerasId, ChannelB);
        collections = ChannelCollections.AddChannel(collections, CamerasId, ChannelA);

        Assert.Equal([ChannelA, ChannelB], collections.Single(c => c.Id == NewsId).ChannelIds);
        Assert.Equal([ChannelB, ChannelA], collections.Single(c => c.Id == CamerasId).ChannelIds);
        Assert.Equal([NewsId, CamerasId], ChannelCollections.MembershipOf(collections, ChannelA));

        collections = ChannelCollections.RemoveChannel(collections, NewsId, ChannelA);
        Assert.Equal([ChannelB], collections.Single(c => c.Id == NewsId).ChannelIds);
        Assert.Equal([ChannelB, ChannelA], collections.Single(c => c.Id == CamerasId).ChannelIds);
    }

    [Fact]
    public void Delete_RemovesOnlyTheCollection()
    {
        var collections = ChannelCollections.Create(
            ChannelCollections.Create([], "News", NewsId)!, "Cameras", CamerasId)!;
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelA);

        var afterDelete = ChannelCollections.Delete(collections, NewsId);

        Assert.Equal([CamerasId], afterDelete.Select(collection => collection.Id));
    }

    [Fact]
    public void RemoveChannelEverywhere_LeavesEmptyCollectionsInPlace()
    {
        var collections = ChannelCollections.Create(
            ChannelCollections.Create([], "News", NewsId)!, "Cameras", CamerasId)!;
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelA);
        collections = ChannelCollections.AddChannel(collections, CamerasId, ChannelA);

        var afterDelete = ChannelCollections.RemoveChannelEverywhere(collections, ChannelA);

        Assert.Equal(2, afterDelete.Count);
        Assert.All(afterDelete, collection => Assert.Empty(collection.ChannelIds));
    }

    [Fact]
    public void Prune_DropsOnlyMissingIdsAndKeepsOrder()
    {
        var collections = ChannelCollections.Create([], "News", NewsId)!;
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelA);
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelB);
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelC);

        // A refresh pruned B; A and C survive with their relative order intact.
        var pruned = ChannelCollections.Prune(collections, [ChannelC, ChannelA]);

        Assert.Equal([ChannelA, ChannelC], pruned.Single().ChannelIds);
    }

    [Fact]
    public void Members_ResolvesInSavedOrderAndSkipsMissing()
    {
        var collections = ChannelCollections.Create([], "News", NewsId)!;
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelC);
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelA);
        collections = ChannelCollections.AddChannel(collections, NewsId, ChannelB);

        var members = ChannelCollections.Members(collections.Single(), [Channel(ChannelA), Channel(ChannelC)]);

        Assert.Equal([ChannelC, ChannelA], members.Select(channel => channel.Id));
    }

    private static StreamChannel Channel(Guid id) => new()
    {
        Id = id,
        Url = $"https://example.test/{id:N}",
        Title = "Title",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero)
    };
}
