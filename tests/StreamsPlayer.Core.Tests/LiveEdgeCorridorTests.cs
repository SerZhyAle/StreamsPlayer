using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0078 risk 3: the engine that carries this rule is opt-in and rarely switched on, so the corridor
/// has to be checkable without it. What is checkable is the arithmetic the engine applies to these three
/// numbers - the shipped corridor's worst case, and the invariants that keep the correction gradual.
/// </summary>
public sealed class LiveEdgeCorridorTests
{
    [Fact]
    public void TheShippedCorridor_CostsAtMostDoubleSpeed()
    {
        // The accepted price of leaving the buffer at the engine's own derivation (2026-08-08). Pinned so
        // that widening the buffer or lowering the target - either of which raises this - is a decision
        // and not a side effect of editing one number.
        Assert.Equal(2.0, LiveEdgeCorridor.Default.PeakSpeed);
    }

    [Fact]
    public void TheShippedCorridor_NeverReachesTheQueueDiscard()
    {
        Assert.True(LiveEdgeCorridor.Default.PeakSpeed < LiveEdgeCorridor.QueueFlushSpeed);
    }

    [Fact]
    public void ABufferFourTimesTheTarget_WouldReachTheQueueDiscard()
    {
        // The failure the test above is guarding, made visible: it is the buffer-to-target ratio and not
        // the corridor's width that decides whether a correction is played out or jumped over.
        var reckless = new LiveEdgeCorridor(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));
        Assert.True(reckless.PeakSpeed >= LiveEdgeCorridor.QueueFlushSpeed);
    }

    [Fact]
    public void TheShippedCorridor_HasAFloorBelowItsTarget()
    {
        var corridor = LiveEdgeCorridor.Default;
        // A floor at or above the target leaves nothing to drain into: the engine would correct and stand
        // down on consecutive frames, which is a rule you can hear.
        Assert.True(corridor.Floor > TimeSpan.Zero);
        Assert.True(corridor.Floor < corridor.Target);
    }

    [Fact]
    public void TheShippedBuffer_IsOneTheEngineWillHonour()
    {
        var corridor = LiveEdgeCorridor.Default;
        // The engine raises its buffer to twice the target and never below it, so a smaller number here
        // would be a claim the engine ignores - the rule would still read as configured while the logged
        // buffer disagreed with it.
        Assert.True(corridor.Buffer >= corridor.Target * 2);
    }

    [Fact]
    public void ANarrowerCorridor_DoesNotBuyAGentlerCorrection()
    {
        // Stated so a future "make it less noticeable" attempt is not made by moving the floor: the
        // engine's 1.1x floor is not ours to lower, and narrowing only changes how often it is reached.
        var narrow = new LiveEdgeCorridor(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11));
        Assert.Equal(LiveEdgeCorridor.GentlestSpeed, narrow.PeakSpeed, 3);
    }

    [Fact]
    public void Describe_SpellsEveryNumberTheEngineWasGivenAndTheirCost()
    {
        Assert.Equal(
            "target_ms=10000 floor_ms=6000 buffer_ms=20000 peak_speed=2.0",
            LiveEdgeCorridor.Default.Describe());
    }
}
