using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class RandomStationHuntPolicyTests
{
    [Fact]
    public void Default_PinsTheShippedCeilingAndDeadline()
    {
        // Pinned so that changing how patient the command is stays a decision rather than a side effect
        // of editing the loop that reads these.
        Assert.Equal(5, RandomStationHuntPolicy.Default.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(10), RandomStationHuntPolicy.Default.ConnectTimeout);
    }

    [Fact]
    public void Default_IsUsable()
    {
        // A zero ceiling makes the command a no-op; a zero deadline fails every station instantly. Neither
        // fails a build, and both look like "the feature is broken" rather than "a constant is wrong".
        Assert.True(RandomStationHuntPolicy.Default.MaxAttempts >= 1);
        Assert.True(RandomStationHuntPolicy.Default.ConnectTimeout > TimeSpan.Zero);
    }
}
