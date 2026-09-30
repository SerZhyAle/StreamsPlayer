using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class AudioStallDetectorTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void SteadyFlow_IsNeverAStall()
    {
        var detector = new AudioStallDetector();
        for (var second = 0; second <= 120; second += 2)
        {
            Assert.False(detector.Observe(At(second), bytesRead: 1_000 + second * 16_000L));
        }
    }

    [Fact]
    public void FlowThatStops_IsReportedOnceAfterTheBound()
    {
        var detector = new AudioStallDetector();
        Assert.False(detector.Observe(At(0), 500_000));
        Assert.False(detector.Observe(At(2), 532_000)); // last growth

        var bound = 2 + AudioStallDetector.StallAfter.TotalSeconds;
        Assert.False(detector.Observe(At(bound - 0.5), 532_000));
        Assert.True(detector.Observe(At(bound), 532_000));
    }

    [Fact]
    public void AStallIsReportedOncePerSilence()
    {
        var detector = new AudioStallDetector();
        detector.Observe(At(0), 100);
        var stalledAt = AudioStallDetector.StallAfter.TotalSeconds;
        Assert.True(detector.Observe(At(stalledAt), 100));

        // The window restarts: the caller is recovering, and the same silence must not raise a second stall.
        Assert.False(detector.Observe(At(stalledAt + 2), 100));
        Assert.False(detector.Observe(At(stalledAt + 4), 100));
    }

    [Fact]
    public void ABriefPause_InsideTheBound_IsNotAStall()
    {
        var detector = new AudioStallDetector();
        detector.Observe(At(0), 100);
        Assert.False(detector.Observe(At(10), 100));
        Assert.False(detector.Observe(At(12), 400)); // data resumed
        Assert.False(detector.Observe(At(24), 400)); // 12 s since the resume, under the bound
    }

    [Fact]
    public void AFirstObservation_OnlyEstablishesTheBaseline()
    {
        // A detector created long after the session clock started must not read an unknown past as silence.
        var detector = new AudioStallDetector();
        Assert.False(detector.Observe(At(600), 0));
        Assert.False(detector.Observe(At(602), 0));
    }

    [Fact]
    public void ACounterThatRestarts_IsNotAStall()
    {
        var detector = new AudioStallDetector();
        detector.Observe(At(0), 9_000_000);
        Assert.False(detector.Observe(At(AudioStallDetector.StallAfter.TotalSeconds + 1), 1_000));
    }
}
