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
}
