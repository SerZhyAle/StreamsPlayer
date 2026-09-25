using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class LivePlaybackNavigationTests
{
    [Fact]
    public void NextAvailable_ReturnsImmediateNeighbour()
    {
        var available = new[] { true, true, true };
        Assert.Equal(1, LivePlaybackNavigation.NextAvailable(0, available));
    }

    [Fact]
    public void NextAvailable_SkipsUnavailableRows()
    {
        var available = new[] { true, false, false, true };
        Assert.Equal(3, LivePlaybackNavigation.NextAvailable(0, available));
    }

    [Fact]
    public void NextAvailable_StopsAtEndWithoutWrapping()
    {
        var available = new[] { true, true, true };
        Assert.Null(LivePlaybackNavigation.NextAvailable(2, available));
    }

    [Fact]
    public void NextAvailable_StopsWhenOnlyRemainingRowsAreUnavailable()
    {
        var available = new[] { true, false, false };
        Assert.Null(LivePlaybackNavigation.NextAvailable(0, available));
    }

    [Fact]
    public void NextAvailable_FromMissingCurrentScansFromStart()
    {
        var available = new[] { false, true, true };
        Assert.Equal(1, LivePlaybackNavigation.NextAvailable(-1, available));
    }

    [Fact]
    public void PreviousAvailable_SkipsUnavailableRows()
    {
        var available = new[] { true, false, false, true };
        Assert.Equal(0, LivePlaybackNavigation.PreviousAvailable(3, available));
    }

    [Fact]
    public void PreviousAvailable_StopsAtStartWithoutWrapping()
    {
        var available = new[] { true, true, true };
        Assert.Null(LivePlaybackNavigation.PreviousAvailable(0, available));
    }

    [Fact]
    public void PreviousAvailable_FromMissingCurrentHasNoPrevious()
    {
        var available = new[] { true, true, true };
        Assert.Null(LivePlaybackNavigation.PreviousAvailable(-1, available));
    }

    [Fact]
    public void Navigation_OnEmptyOrderYieldsNothing()
    {
        var available = Array.Empty<bool>();
        Assert.Null(LivePlaybackNavigation.NextAvailable(-1, available));
        Assert.Null(LivePlaybackNavigation.PreviousAvailable(-1, available));
    }

    // SP-0132: the compact panel asks for both neighbours on every volume tick. The availability check
    // behind the predicate is a catalog lookup, so the cost of one refresh must be the neighbours the scan
    // visits, not the length of the captured order - with the full bank that difference was ~10^8 compares.
    [Fact]
    public void PredicateOverloads_AskOnlyAboutTheIndicesTheScanVisits()
    {
        var asked = new List<int>();
        bool IsAvailable(int index)
        {
            asked.Add(index);
            return true;
        }

        Assert.Equal(5_001, LivePlaybackNavigation.NextAvailable(5_000, 20_000, IsAvailable));
        Assert.Equal(4_999, LivePlaybackNavigation.PreviousAvailable(5_000, IsAvailable));

        Assert.Equal([5_001, 4_999], asked);
    }

    [Fact]
    public void PredicateOverloads_SkipUnavailableEntriesLikeTheMask()
    {
        var available = new[] { true, false, false, true, false };

        Assert.Equal(3, LivePlaybackNavigation.NextAvailable(0, available.Length, index => available[index]));
        Assert.Equal(0, LivePlaybackNavigation.PreviousAvailable(3, index => available[index]));
        Assert.Null(LivePlaybackNavigation.NextAvailable(3, available.Length, index => available[index]));
    }
}
