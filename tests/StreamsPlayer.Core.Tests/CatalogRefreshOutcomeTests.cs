using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogRefreshOutcomeTests
{
    [Fact]
    public void Apply_MergesTheDownloadedBankIntoTheStateThatChangedWhileItWasInFlight()
    {
        var dropped = Channel("https://example.test/dropped");
        var initial = new CatalogState { Channels = [dropped] };
        var outcome = new CatalogRefreshOutcome(
            new StreamBank([Entry("https://example.test/offered")], null, true, null),
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        var manual = Channel("https://example.test/manual") with { SourceOrigin = SourceOrigin.Manual };
        var current = initial with
        {
            Channels = [dropped with { Pinned = true }, manual],
            Collections = [new ChannelCollection { Id = Guid.NewGuid(), Name = "Morning", ChannelIds = [dropped.Id] }]
        };

        var applied = outcome.Apply(current);

        Assert.Contains(applied.State.Channels, channel => channel.Id == manual.Id);
        var retired = Assert.Single(applied.State.Channels, channel => channel.Id == dropped.Id);
        Assert.True(retired.Pinned);
        Assert.NotNull(retired.RetiredAt);
        Assert.Contains(dropped.Id, Assert.Single(applied.State.Collections).ChannelIds);
    }

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Station",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = DateTimeOffset.UtcNow
    };

    private static CatalogEntry Entry(string url) =>
        new("Station", url, MediaKind.Audio, null, null, null, null, null, null);
}
