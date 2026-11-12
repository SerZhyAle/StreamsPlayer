using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastWindowRulesTests
{
    [Theory]
    [InlineData(false, false, false, false, BroadcastSourcePhase.Off)]
    [InlineData(false, false, false, true, BroadcastSourcePhase.Failed)]
    [InlineData(false, true, false, true, BroadcastSourcePhase.Starting)]
    [InlineData(true, false, false, false, BroadcastSourcePhase.Live)]
    [InlineData(true, false, true, false, BroadcastSourcePhase.Stopping)]
    [InlineData(false, true, true, false, BroadcastSourcePhase.Stopping)]
    [InlineData(true, false, false, true, BroadcastSourcePhase.Live)]
    public void A_source_is_in_exactly_one_phase(bool running, bool starting, bool stopping, bool failed, BroadcastSourcePhase expected)
    {
        Assert.Equal(expected, BroadcastWindowRules.PhaseOf(running, starting, stopping, failed));
    }

    [Fact]
    public void The_window_lists_every_source_once_with_the_picture_first()
    {
        Assert.Equal(Enum.GetValues<BroadcastSource>().Order(), BroadcastWindowRules.SourceOrder.Order());
        Assert.Equal(BroadcastSource.Screen, BroadcastWindowRules.SourceOrder[0]);
    }

    [Theory]
    [InlineData(1, true, true, false, true)]
    [InlineData(0, true, true, false, false)]
    [InlineData(2, false, true, false, false)]
    [InlineData(1, true, false, false, false)]
    [InlineData(1, true, true, true, false)]
    public void The_warning_mark_needs_a_live_broadcast_an_announcing_account_and_an_unreachable_server(
        int live, bool active, bool mayBroadcast, bool online, bool expected)
    {
        Assert.Equal(expected, BroadcastWindowRules.ShowsServerWarning(live, active, mayBroadcast, online));
    }

    [Theory]
    [InlineData(BroadcastSourcePhase.Off, 0, true)]
    [InlineData(BroadcastSourcePhase.Failed, 3, true)]
    [InlineData(BroadcastSourcePhase.Off, 4, false)]
    [InlineData(BroadcastSourcePhase.Live, 0, false)]
    [InlineData(BroadcastSourcePhase.Starting, 0, false)]
    [InlineData(BroadcastSourcePhase.Stopping, 0, false)]
    public void A_start_is_offered_only_to_an_idle_source_while_a_slot_is_free(BroadcastSourcePhase phase, int busy, bool expected)
    {
        Assert.Equal(expected, BroadcastWindowRules.CanStart(phase, busy));
    }
}
