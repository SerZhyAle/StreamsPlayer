using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class PlaybackReachabilityRulesTests
{
    [Theory]
    [InlineData(PlaybackReachability.NotProbed, true, true)]
    [InlineData(PlaybackReachability.HostReachable, true, true)]
    // The dead host with a working network: no attempt is spent, and the offer stands - the channel is
    // the thing at fault (SP-0041 Decision 3).
    [InlineData(PlaybackReachability.ChannelUnreachable, false, true)]
    // No network: no attempt is spent, and the channel is not offered for removal - it was never reached
    // (SP-0041 Decisions 4 and 5).
    [InlineData(PlaybackReachability.NetworkUnreachable, false, false)]
    public void Rules_MapEveryVerdict(PlaybackReachability reachability, bool spendsBudget, bool allowsRemoval)
    {
        Assert.Equal(spendsBudget, PlaybackReachabilityRules.SpendsRecoveryBudget(reachability));
        Assert.Equal(allowsRemoval, PlaybackReachabilityRules.AllowsChannelRemoval(reachability));
    }

    [Fact]
    public void Default_IsTheBehaviourBeforeTheGate()
    {
        // A caller that never asked keeps the ladder and the offer, exactly as before SP-0041.
        Assert.Equal(PlaybackReachability.NotProbed, default(PlaybackReachability));
    }
}
