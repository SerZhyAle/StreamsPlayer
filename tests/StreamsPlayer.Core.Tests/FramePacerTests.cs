namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0243: the capture loop's schedule - the period is the longer of the interval and the work, never their sum.</summary>
public sealed class FramePacerTests
{
    private const long Interval = 10_000_000 / 30;
    private const long OneSecond = 10_000_000;

    /// <summary>Runs the loop the capture worker runs, on a clock the test advances, and counts the frames taken.</summary>
    private static int Frames(long interval, long work, long horizon)
    {
        var pacer = new FramePacer(interval);
        long now = 0;
        var frames = 0;
        while (true)
        {
            var delay = pacer.Delay(now);
            if (delay > 0) { now += delay; continue; }
            if (now >= horizon) return frames;
            pacer.Started(now);
            now += work;
            frames++;
        }
    }

    [Fact]
    public void WorkShorterThanTheIntervalKeepsTheFullRate()
    {
        var frames = Frames(Interval, work: 100_000, horizon: 10 * OneSecond);

        Assert.InRange(frames, 299, 301);
    }

    [Fact]
    public void WorkLongerThanTheIntervalSetsThePeriodAndIsNotAddedToIt()
    {
        // A 4K read of 66 ms plus conversion and encoding: the old schedule gave 1 / (work + interval) = 10 frames per second.
        var frames = Frames(Interval, work: 400_000, horizon: 10 * OneSecond);

        Assert.InRange(frames, 249, 251);
    }

    [Fact]
    public void AStallIsNotReplayedAsABurst()
    {
        var pacer = new FramePacer(Interval);
        long now = 0;
        pacer.Started(now);
        now += OneSecond;

        Assert.Equal(0, pacer.Delay(now));
        pacer.Started(now);

        Assert.Equal(Interval, pacer.Delay(now));
    }

    [Fact]
    public void ALateWakeUpDoesNotShiftTheGrid()
    {
        var pacer = new FramePacer(Interval);
        pacer.Started(0);
        pacer.Started(Interval + 50_000);

        Assert.Equal(2 * Interval - (Interval + 50_000), pacer.Delay(Interval + 50_000));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AnIntervalThatIsNotPositiveIsRefused(long interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(interval));
    }
}
