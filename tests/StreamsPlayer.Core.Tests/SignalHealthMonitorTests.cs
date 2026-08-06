using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0045 criterion 9: the stripe's state rule, driven as a sequence of inputs with no window, no
/// network and no media backend. The states themselves are hard to provoke on a live stream, which is
/// exactly why the decision was pushed out of the UI and into a type that can be exercised here.
/// </summary>
public sealed class SignalHealthMonitorTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static DecoderLossCounters Lost(long pictures) => new(pictures, 0, 0);

    /// <summary>Puts a monitor in the state a healthy, live stream is in at <paramref name="now"/>.</summary>
    private static SignalHealthMonitor Live(TimeSpan now)
    {
        var monitor = new SignalHealthMonitor();
        monitor.NotifyOpening();
        monitor.NotifyLive();
        Assert.Equal(SignalHealth.Good, monitor.Evaluate(now));
        return monitor;
    }

    [Fact]
    public void BeforeTheFirstLiveReading_TheStateIsUnknown()
    {
        var monitor = new SignalHealthMonitor();
        monitor.NotifyOpening();

        Assert.Equal(SignalHealth.Unknown, monitor.Evaluate(At(0)));
    }

    // Criterion 6: an ordinary first connect that retries once is still connecting, not lost.
    [Fact]
    public void RecoveringBeforeEverLive_IsNotRed()
    {
        var monitor = new SignalHealthMonitor();
        monitor.NotifyOpening();
        monitor.NotifyRecovering(At(2));

        Assert.Equal(SignalHealth.Unknown, monitor.Evaluate(At(2)));
    }

    // Criterion 5: a channel that never played and then failed is red, not stuck at Unknown.
    [Fact]
    public void FailingBeforeEverLive_IsRed()
    {
        var monitor = new SignalHealthMonitor();
        monitor.NotifyOpening();
        monitor.NotifyFailed(At(4));

        Assert.Equal(SignalHealth.Lost, monitor.Evaluate(At(4)));
    }

    // Criterion 1: an undisturbed stream is green as soon as it is live - there is nothing to wait out.
    [Fact]
    public void AnUndisturbedStream_IsGreenImmediately()
    {
        var monitor = Live(At(3));

        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(600)));
    }

    // Criterion 2, and decision 4: green is earned back, not granted.
    [Fact]
    public void AfterADisturbance_YellowHoldsForTheWholeCleanInterval()
    {
        var monitor = Live(At(10));
        monitor.NotifyDisturbance(At(20));

        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(20)));
        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(20) + SignalHealthMonitor.CleanInterval - At(1)));
        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(20) + SignalHealthMonitor.CleanInterval));
    }

    // The anti-flicker constraint, stated as the case that set the interval.
    [Fact]
    public void AStreamDippingOncePerMinute_NeverReturnsToGreen()
    {
        var monitor = Live(At(0));

        for (var minute = 1; minute <= 10; minute++)
        {
            var dip = At(minute * 59);
            monitor.NotifyDisturbance(dip);
            Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(dip));
            Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(dip + At(58)));
        }
    }

    // A long stall is one continuous yellow, not a bar that goes green in the middle of it.
    [Fact]
    public void RepeatedDisturbances_RestartTheCleanInterval()
    {
        var monitor = Live(At(0));
        monitor.NotifyDisturbance(At(10));
        monitor.NotifyDisturbance(At(50));

        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(80)));
        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(50) + SignalHealthMonitor.CleanInterval));
    }

    // Criterion 4 and decision 8: red while reconnecting, yellow on return, green one interval later.
    [Fact]
    public void GreenYellowRedAndBack()
    {
        var monitor = Live(At(0));

        monitor.NotifyDisturbance(At(30));
        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(30)));

        monitor.NotifyRecovering(At(35));
        Assert.Equal(SignalHealth.Lost, monitor.Evaluate(At(40)));

        monitor.NotifyOpening();
        Assert.Equal(SignalHealth.Lost, monitor.Evaluate(At(41)));

        monitor.NotifyLive();
        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(42)));
        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(35) + SignalHealthMonitor.CleanInterval));
    }

    // Criterion 3, with the noise floor: startup jitter must not paint the stream yellow.
    [Fact]
    public void LossBelowTheThreshold_StaysGreen()
    {
        var monitor = Live(At(0));
        monitor.NotifySample(At(2), Lost(0));
        monitor.NotifySample(At(4), Lost(SignalHealthMonitor.LossThreshold - 1));

        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(4)));
    }

    [Fact]
    public void LossAtTheThreshold_IsYellow()
    {
        var monitor = Live(At(0));
        monitor.NotifySample(At(2), Lost(0));
        monitor.NotifySample(At(4), Lost(SignalHealthMonitor.LossThreshold));

        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(4)));
        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(4) + SignalHealthMonitor.CleanInterval));
    }

    // The three counters answer one question, so they share one threshold (decision 7).
    [Fact]
    public void TheThreeCountersAreSummed()
    {
        var monitor = Live(At(0));
        monitor.NotifySample(At(2), new DecoderLossCounters(0, 0, 0));
        monitor.NotifySample(At(4), new DecoderLossCounters(2, 2, 1));

        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(4)));
    }

    // The first sample after an open only establishes a baseline; totals are not deltas.
    [Fact]
    public void TheFirstSampleOfAMedia_OnlyEstablishesTheBaseline()
    {
        var monitor = Live(At(0));
        monitor.NotifySample(At(2), Lost(4_000));

        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(2)));
    }

    // A reconnect restarts the engine's counters; differencing across that boundary would invent trouble.
    [Fact]
    public void ACounterResetAcrossAnOpen_IsNotADisturbance()
    {
        var monitor = Live(At(0));
        monitor.NotifySample(At(2), Lost(0));
        monitor.NotifySample(At(4), Lost(900));
        Assert.Equal(SignalHealth.Degraded, monitor.Evaluate(At(4)));

        monitor.NotifyOpening();
        monitor.NotifyLive();
        monitor.NotifySample(At(6), Lost(0));
        monitor.NotifySample(At(8), Lost(1));

        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(4) + SignalHealthMonitor.CleanInterval));
    }

    // Criterion 8: the backend without counters must not read as permanently degraded.
    [Fact]
    public void ABackendWithoutCounters_StaysGreen()
    {
        var monitor = Live(At(0));

        for (var sample = 1; sample <= 100; sample++)
        {
            monitor.NotifySample(At(sample * 2), null);
        }

        Assert.Equal(SignalHealth.Good, monitor.Evaluate(At(200)));
    }
}
