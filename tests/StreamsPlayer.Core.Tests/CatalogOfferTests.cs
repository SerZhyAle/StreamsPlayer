using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogOfferTests
{
    [Fact]
    public void Exclusion_AnOrdinaryRowIsOffered()
    {
        Assert.Equal(CatalogExclusion.None, CatalogOffer.Exclusion(Channel(), hideAdultContent: true, browsingCollection: false));
    }

    [Fact]
    public void Exclusion_AnAdultRowIsKeptOutOnlyWhileTheSettingIsOn()
    {
        var adult = Channel() with { Topic = "adult" };

        Assert.Equal(CatalogExclusion.AdultHidden, CatalogOffer.Exclusion(adult, hideAdultContent: true, browsingCollection: true));
        Assert.Equal(CatalogExclusion.None, CatalogOffer.Exclusion(adult, hideAdultContent: false, browsingCollection: false));
    }

    // SP-0089: a retired row stays where the user put it - the pinned strip, or the collection being
    // browsed - and leaves only the general list.
    [Fact]
    public void Exclusion_ARetiredRowLeavesOnlyTheGeneralList()
    {
        var retired = Channel() with { RetiredAt = DateTimeOffset.UnixEpoch };

        Assert.Equal(CatalogExclusion.Retired, CatalogOffer.Exclusion(retired, hideAdultContent: false, browsingCollection: false));
        Assert.Equal(CatalogExclusion.None, CatalogOffer.Exclusion(retired, hideAdultContent: false, browsingCollection: true));
        Assert.Equal(CatalogExclusion.None,
            CatalogOffer.Exclusion(retired with { Pinned = true }, hideAdultContent: false, browsingCollection: false));
    }

    private static StreamChannel Channel() => new()
    {
        Id = Guid.NewGuid(),
        Url = "https://example.com/radio",
        Title = "Radio",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = DateTimeOffset.UnixEpoch
    };
}
