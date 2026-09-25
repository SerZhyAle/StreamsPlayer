using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0120 requirement 5: an event raised by a radio connection that has since been replaced or stopped is
/// ignored, so it can neither stop the connection playing now nor spend its recovery budget a second time.
/// </summary>
public sealed class PlaybackConnectionSequenceTests
{
    [Fact]
    public void AnEventFromTheConnectionPlayingNowIsCurrent()
    {
        var connections = new PlaybackConnectionSequence();

        var playing = connections.Open();

        Assert.True(connections.IsCurrent(playing));
    }

    [Fact]
    public void AnEventStampedByASupersededConnectionIsIgnored()
    {
        var connections = new PlaybackConnectionSequence();
        var previous = connections.Open();

        var replacement = connections.Open();

        Assert.False(connections.IsCurrent(previous));
        Assert.True(connections.IsCurrent(replacement));
    }

    [Fact]
    public void AnEventStampedByAStoppedConnectionIsIgnored()
    {
        var connections = new PlaybackConnectionSequence();
        var stopped = connections.Open();

        connections.Close();

        Assert.False(connections.IsCurrent(stopped));
    }

    [Fact]
    public void AnIdentityIsNeverIssuedAgainAfterAStop()
    {
        var connections = new PlaybackConnectionSequence();
        var first = connections.Open();
        connections.Close();

        var second = connections.Open();

        Assert.NotEqual(first, second);
        Assert.False(connections.IsCurrent(first));
        Assert.True(connections.IsCurrent(second));
    }

    [Fact]
    public void AnUnstampedEventIsNeverCurrent()
    {
        var connections = new PlaybackConnectionSequence();

        Assert.False(connections.IsCurrent(default));
        connections.Open();
        Assert.False(connections.IsCurrent(default));
    }
}
