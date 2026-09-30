using System.Net.Sockets;
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
    // SP-0168: a name the resolver says does not exist is a definite answer with its own reason.
    [InlineData(PlaybackReachability.NameNotResolved, false, true)]
    // SP-0168: a timeout proves nothing - the ladder runs as if nothing had been asked.
    [InlineData(PlaybackReachability.Inconclusive, true, true)]
    public void Rules_MapEveryVerdict(PlaybackReachability reachability, bool spendsBudget, bool allowsRemoval)
    {
        Assert.Equal(spendsBudget, PlaybackReachabilityRules.SpendsRecoveryBudget(reachability));
        Assert.Equal(allowsRemoval, PlaybackReachabilityRules.AllowsChannelRemoval(reachability));
    }

    [Fact]
    public void Rules_CoverEveryVerdict()
    {
        // A new verdict must be given a consequence here, not inherit one by accident.
        Assert.Equal(6, Enum.GetValues<PlaybackReachability>().Length);
    }

    [Theory]
    [InlineData(ConnectOutcome.Connected, false, PlaybackReachability.HostReachable)]
    [InlineData(ConnectOutcome.Connected, true, PlaybackReachability.HostReachable)]
    // Timeout -> the ladder continues, for a public and a local host alike.
    [InlineData(ConnectOutcome.Inconclusive, false, PlaybackReachability.Inconclusive)]
    [InlineData(ConnectOutcome.Inconclusive, true, PlaybackReachability.Inconclusive)]
    // Refusal -> unreachable; a local host that refuses is "never reached", not "broken".
    [InlineData(ConnectOutcome.Refused, false, PlaybackReachability.ChannelUnreachable)]
    [InlineData(ConnectOutcome.Refused, true, PlaybackReachability.NetworkUnreachable)]
    // Name not resolved -> unreachable with its own reason; a local short name is never blamed.
    [InlineData(ConnectOutcome.NameNotResolved, false, PlaybackReachability.NameNotResolved)]
    [InlineData(ConnectOutcome.NameNotResolved, true, PlaybackReachability.NetworkUnreachable)]
    public void Verdict_MapsEveryOutcome(ConnectOutcome outcome, bool isLocal, PlaybackReachability expected)
    {
        Assert.Equal(expected, PlaybackReachabilityRules.Verdict(outcome, isLocal));
    }

    [Theory]
    [InlineData(SocketError.Success, ConnectOutcome.Connected)]
    [InlineData(SocketError.ConnectionRefused, ConnectOutcome.Refused)]
    [InlineData(SocketError.HostNotFound, ConnectOutcome.NameNotResolved)]
    [InlineData(SocketError.NoData, ConnectOutcome.NameNotResolved)]
    // A transient resolver failure, a socket timeout and routing errors prove nothing.
    [InlineData(SocketError.TryAgain, ConnectOutcome.Inconclusive)]
    [InlineData(SocketError.TimedOut, ConnectOutcome.Inconclusive)]
    [InlineData(SocketError.HostUnreachable, ConnectOutcome.Inconclusive)]
    [InlineData(SocketError.NetworkUnreachable, ConnectOutcome.Inconclusive)]
    [InlineData(SocketError.ConnectionReset, ConnectOutcome.Inconclusive)]
    public void OutcomeOf_SeparatesRefusedTimedOutAndUnresolved(SocketError error, ConnectOutcome expected)
    {
        Assert.Equal(expected, PlaybackReachabilityRules.OutcomeOf(error));
    }

    [Theory]
    [InlineData(ConnectOutcome.Inconclusive)]
    [InlineData(ConnectOutcome.Connected)]
    public void Verdict_TimeoutOrConnectNeverSkipsTheLadder(ConnectOutcome outcome)
    {
        Assert.True(PlaybackReachabilityRules.SpendsRecoveryBudget(PlaybackReachabilityRules.Verdict(outcome, false)));
        Assert.True(PlaybackReachabilityRules.SpendsRecoveryBudget(PlaybackReachabilityRules.Verdict(outcome, true)));
    }

    [Fact]
    public void Default_IsTheBehaviourBeforeTheGate()
    {
        // A caller that never asked keeps the ladder and the offer, exactly as before SP-0041.
        Assert.Equal(PlaybackReachability.NotProbed, default(PlaybackReachability));
    }
}
