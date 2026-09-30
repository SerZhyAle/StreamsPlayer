using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0126: an edit makes a bank row the user's - published or imported alike - and no later merge may
/// write over it or bring its original address back as a second, visible row.
/// </summary>
public sealed class ChannelOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly CatalogMergeOptions BankFileImport = new(
        RemoveMissing: false,
        FaviconSource: FaviconSource.Imported,
        TargetOrigin: SourceOrigin.LocalCatalog);

    public static TheoryData<SourceOrigin, CatalogMergeOptions> BankMerges => new()
    {
        { SourceOrigin.LocalCatalog, BankFileImport },
        { SourceOrigin.Catalog, CatalogMergeOptions.CatalogRefresh }
    };

    [Theory]
    [MemberData(nameof(BankMerges))]
    public void EditedTitle_SurvivesTheSameBankUnchanged(SourceOrigin origin, CatalogMergeOptions merge)
    {
        var bank = Entry("Bank title", "https://example.test/one");
        var state = MergeInto(new CatalogState(), [bank], merge);
        var id = Assert.Single(state.Channels).Id;

        var edited = ChannelOwnership.ApplyEdit(state, id, owned => owned with { Title = "My title" });
        var remerged = MergeInto(edited, [bank], merge);

        var channel = Assert.Single(remerged.Channels);
        Assert.Equal(Assert.Single(edited.Channels), channel);
        Assert.Equal("My title", channel.Title);
        Assert.Equal(SourceOrigin.Manual, channel.SourceOrigin);
        Assert.NotEqual(origin, channel.SourceOrigin);
        Assert.Empty(edited.HiddenCatalogUrls);
    }

    [Theory]
    [MemberData(nameof(BankMerges))]
    public void EditedAddress_SurvivesTheSameBankWithoutAVisibleDuplicate(SourceOrigin origin, CatalogMergeOptions merge)
    {
        var bank = Entry("Bank title", "https://example.test/one");
        var state = MergeInto(new CatalogState(), [bank], merge);
        var original = Assert.Single(state.Channels);
        Assert.Equal(origin, original.SourceOrigin);

        var edited = ChannelOwnership.ApplyEdit(
            state,
            original.Id,
            owned => owned with { Title = "Mirror", Url = "https://mirror.test/one" });
        var remerged = MergeInto(edited, [bank], merge);

        var mine = Assert.Single(remerged.Channels, channel => channel.Id == original.Id);
        Assert.Equal(Assert.Single(edited.Channels), mine);
        var hidden = HiddenSet(remerged);
        var visible = remerged.Channels.Where(channel => !ChannelOwnership.IsHidden(hidden, channel)).ToList();
        Assert.Equal(mine, Assert.Single(visible));
    }

    [Fact]
    public void BankRowEdit_DropsTheFaviconIndex()
    {
        var state = MergeInto(new CatalogState(), [Entry("Bank", "https://example.test/one") with { FaviconIndex = 3 }], BankFileImport);
        var id = Assert.Single(state.Channels).Id;

        var edited = ChannelOwnership.ApplyEdit(state, id, owned => owned with { Title = "Mine" });

        Assert.Null(Assert.Single(edited.Channels).FaviconIndex);
    }

    [Theory]
    [InlineData(SourceOrigin.Manual)]
    [InlineData(SourceOrigin.Imported)]
    public void UserRowEdit_KeepsItsOriginAndHidesNothing(SourceOrigin origin)
    {
        var channel = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = "https://example.test/mine",
            Title = "Mine",
            MediaKind = MediaKind.Audio,
            SourceOrigin = origin,
            AddedAt = Now
        };

        var edited = ChannelOwnership.ApplyEdit(
            new CatalogState { Channels = [channel] },
            channel.Id,
            owned => owned with { Url = "https://example.test/moved" });

        var result = Assert.Single(edited.Channels);
        Assert.Equal(origin, result.SourceOrigin);
        Assert.Equal("https://example.test/moved", result.Url);
        Assert.Empty(edited.HiddenCatalogUrls);
    }

    [Fact]
    public void UnknownId_LeavesTheStateAlone()
    {
        var state = MergeInto(new CatalogState(), [Entry("Bank", "https://example.test/one")], BankFileImport);

        Assert.Same(state, ChannelOwnership.ApplyEdit(state, Guid.NewGuid(), owned => owned with { Title = "X" }));
    }

    // SP-0177: no merge can revive a user row, so an edit that kept the date would retire the row for good.
    [Fact]
    public void Edit_ClearsRetirement()
    {
        var state = MergeInto(new CatalogState(), [Entry("Bank", "https://example.test/one")], CatalogMergeOptions.CatalogRefresh);
        var retired = Assert.Single(state.Channels) with { Pinned = true, RetiredAt = Now };
        state = state with { Channels = [retired] };

        var edited = ChannelOwnership.ApplyEdit(state, retired.Id, owned => owned with { Url = "https://mirror.test/one" });

        var channel = Assert.Single(edited.Channels);
        Assert.Null(channel.RetiredAt);
        Assert.True(channel.Pinned);
    }

    private static CatalogState MergeInto(CatalogState state, IReadOnlyList<CatalogEntry> entries, CatalogMergeOptions options) =>
        state with { Channels = [.. CatalogMerger.Merge(state.Channels, entries, Now, options).Channels] };

    private static HashSet<string> HiddenSet(CatalogState state) =>
        new(state.HiddenCatalogUrls.Select(CatalogUrlIdentity.Normalize), StringComparer.Ordinal);

    private static CatalogEntry Entry(string title, string url) =>
        new(title, url, MediaKind.Audio, "News", "World", "english", "MT", null, null);
}
