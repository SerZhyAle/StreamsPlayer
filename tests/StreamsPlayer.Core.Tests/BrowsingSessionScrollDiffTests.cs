namespace StreamsPlayer.Core.Tests;

public sealed class BrowsingSessionScrollDiffTests
{
    [Fact]
    public void DiffersOnlyByScrollOffset_WhenNothingElseMoved_IsTrue()
    {
        var session = new BrowsingSession { SearchQuery = "jazz", ScrollOffset = 1200 };

        Assert.True((session with { ScrollOffset = 1800 }).DiffersOnlyByScrollOffset(session));
    }

    [Fact]
    public void DiffersOnlyByScrollOffset_WhenTheOffsetIsUnchanged_IsFalse()
    {
        var session = new BrowsingSession { ScrollOffset = 1200 };

        Assert.False(session.DiffersOnlyByScrollOffset(session));
    }

    [Fact]
    public void DiffersOnlyByScrollOffset_WhenADeliberateFieldMovedToo_IsFalse()
    {
        var session = new BrowsingSession { SearchQuery = "jazz", ScrollOffset = 1200 };

        // A filter, a sort or a keystroke must never be rate-limited as if it were a scroll.
        Assert.False((session with { ScrollOffset = 1800, CountryFilter = "UA" }).DiffersOnlyByScrollOffset(session));
        Assert.False((session with { ScrollOffset = 1800, SearchQuery = "jazzy" }).DiffersOnlyByScrollOffset(session));
        Assert.False((session with { ScrollOffset = 1800, SortMode = "Bitrate" }).DiffersOnlyByScrollOffset(session));
        Assert.False((session with { ScrollOffset = 1800, LastSelectedChannelId = Guid.NewGuid() }).DiffersOnlyByScrollOffset(session));
    }
}
