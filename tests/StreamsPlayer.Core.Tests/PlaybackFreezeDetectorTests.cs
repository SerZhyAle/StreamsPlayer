using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0070 criteria 1-4: the freeze rule, driven as a sequence of observations with no window, no
/// network and no media backend. The situation it exists for - a source whose input dies while the
/// engine's clock keeps running - cannot be provoked on demand against a live stream, which is exactly
/// why the decision was pushed out of the player and into a type that can be exercised here.
/// </summary>
public sealed class PlaybackFreezeDetectorTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static PlaybackProgressCounters Counters(long displayed, long bytes) => new(displayed, bytes);

    /// <summary>
    /// Drives a healthy video leg from a cold detector up to <paramref name="until"/> seconds, three
    /// seconds per observation, and returns it with the picture latch set - the state every "and then it
    /// froze" test starts from.
    /// </summary>
    private static PlaybackFreezeDetector PlayingVideo(double until, out long positionMs, out PlaybackProgressCounters counters)
    {
        var detector = new PlaybackFreezeDetector();
        positionMs = 0;
        counters = Counters(0, 0);
        for (var second = 0d; second <= until; second += 3)
        {
            if (second > 0)
            {
                positionMs += 3_000;
                counters = Counters(counters.DisplayedPictures + 72, counters.InputBytes + 750_000);
            }

            Assert.False(detector.Observe(At(second), isPlaying: true, positionMs, counters));
        }

        // The out values are the last readings actually observed, so a caller can hold them still and
        // have that mean "nothing moved since" rather than "one unobserved sample of progress".
        return detector;
    }

    // Criterion 1: the reported failure. Pictures stop reaching the screen and no bytes arrive, while
    // media time keeps advancing because the engine is playing out silence - the case the old
    // position-only watchdog scored as healthy for twenty-two seconds at a time.
    [Fact]
    public void NoPicturesAndNoBytes_WhileMediaTimeStillAdvances_Freezes()
    {
        var detector = PlayingVideo(6, out var positionMs, out var counters);

        // 9 s: the last progress was observed at 6 s, so this is the first observation past the threshold.
        Assert.False(detector.Observe(At(9), isPlaying: true, positionMs + 3_000, counters));
        Assert.False(detector.Observe(At(12), isPlaying: true, positionMs + 6_000, counters));
        Assert.True(detector.Observe(At(15), isPlaying: true, positionMs + 9_000, counters));
    }

    // Criterion 2, first half: a stream that is still receiving data is rebuffering, not frozen, no
    // matter how long the picture has been still.
    [Fact]
    public void BytesStillArriving_NeverFreezes()
    {
        var detector = PlayingVideo(6, out var positionMs, out var counters);

        for (var second = 9d; second <= 60; second += 3)
        {
            counters = Counters(counters.DisplayedPictures, counters.InputBytes + 120_000);
            Assert.False(detector.Observe(At(second), isPlaying: true, positionMs, counters));
        }
    }

    // Criterion 2, second half: pictures still reaching the screen is progress even if the byte total
    // stalls between two samples, which it does whenever a segment boundary falls between them.
    [Fact]
    public void PicturesStillDisplayed_NeverFreezes()
    {
        var detector = PlayingVideo(6, out var positionMs, out var counters);

        for (var second = 9d; second <= 60; second += 3)
        {
            counters = Counters(counters.DisplayedPictures + 24, counters.InputBytes);
            Assert.False(detector.Observe(At(second), isPlaying: true, positionMs, counters));
        }
    }

    // Criterion 3: a stream that never had a picture cannot lose one. Without the latch, an audio-only
    // stream would report a freeze nine seconds in, every time, while playing perfectly.
    [Fact]
    public void AStreamThatNeverDisplayedAPicture_JudgesByMediaTime()
    {
        var detector = new PlaybackFreezeDetector();
        var positionMs = 0L;
        var counters = Counters(0, 0);

        for (var second = 0d; second <= 60; second += 3)
        {
            Assert.False(detector.Observe(At(second), isPlaying: true, positionMs, counters));
            positionMs += 3_000;
            counters = Counters(0, counters.InputBytes + 40_000);
        }
    }

    // The same audio-only leg, actually stuck: media time is the signal it does have, so the fallback
    // still protects it.
    [Fact]
    public void AStreamThatNeverDisplayedAPicture_FreezesWhenMediaTimeStops()
    {
        var detector = new PlaybackFreezeDetector();

        Assert.False(detector.Observe(At(0), isPlaying: true, 10_000, Counters(0, 40_000)));
        Assert.False(detector.Observe(At(3), isPlaying: true, 13_000, Counters(0, 80_000)));
        Assert.False(detector.Observe(At(6), isPlaying: true, 13_000, Counters(0, 80_000)));
        Assert.False(detector.Observe(At(9), isPlaying: true, 13_000, Counters(0, 80_000)));
        Assert.True(detector.Observe(At(12), isPlaying: true, 13_000, Counters(0, 80_000)));
    }

    // A backend with no counters keeps exactly the watchdog it had. Null is "no telemetry", and the one
    // thing it must not mean is "no watchdog".
    [Fact]
    public void WithoutCounters_StalledMediaTimeStillFreezes()
    {
        var detector = new PlaybackFreezeDetector();

        // 100 ms per three seconds is the shape a stalled live stream actually has: the position creeps
        // rather than standing exactly still, which is why the test is a threshold and not an equality.
        Assert.False(detector.Observe(At(0), isPlaying: true, 5_000, counters: null));
        Assert.False(detector.Observe(At(3), isPlaying: true, 5_100, counters: null));
        Assert.False(detector.Observe(At(6), isPlaying: true, 5_200, counters: null));
        Assert.True(detector.Observe(At(9), isPlaying: true, 5_300, counters: null));
    }

    [Fact]
    public void WithoutCounters_AdvancingMediaTimeNeverFreezes()
    {
        var detector = new PlaybackFreezeDetector();
        var positionMs = 0L;

        for (var second = 0d; second <= 60; second += 3)
        {
            Assert.False(detector.Observe(At(second), isPlaying: true, positionMs, counters: null));
            positionMs += 3_000;
        }
    }

    // Criterion 4's engine half: an engine that is not playing is not a stream that stopped moving.
    // Recovery and closing are guarded by the caller; this is the paused/stopped case.
    [Fact]
    public void WhileNotPlaying_NeverFreezesAndTheWindowRestarts()
    {
        var detector = PlayingVideo(6, out var positionMs, out var counters);

        for (var second = 9d; second <= 60; second += 3)
        {
            Assert.False(detector.Observe(At(second), isPlaying: false, positionMs, counters));
        }

        // Resuming does not inherit the paused time: the freeze threshold starts from the resume, so a
        // window that spent a minute paused does not report a freeze on its first tick back.
        Assert.False(detector.Observe(At(63), isPlaying: true, positionMs, counters));
        Assert.False(detector.Observe(At(66), isPlaying: true, positionMs, counters));
        Assert.True(detector.Observe(At(69), isPlaying: true, positionMs, counters));
    }

    // A new media restarts the engine's totals from zero. Differencing across that boundary would score
    // the reset as "nothing arrived" and tear down a stream that just started.
    [Fact]
    public void AfterReset_CountersStartingOverDoNotFabricateAFreeze()
    {
        var detector = PlayingVideo(6, out _, out _);
        detector.Reset();

        var positionMs = 0L;
        var counters = Counters(0, 0);
        for (var second = 9d; second <= 60; second += 3)
        {
            Assert.False(detector.Observe(At(second), isPlaying: true, positionMs, counters));
            positionMs += 3_000;
            counters = Counters(counters.DisplayedPictures + 72, counters.InputBytes + 750_000);
        }
    }

    // The same reset without the caller's help: counters that go backwards mid-leg are a restarted
    // media, not a freeze.
    [Fact]
    public void CountersGoingBackwards_AreNotAFreeze()
    {
        var detector = PlayingVideo(6, out var positionMs, out _);

        Assert.False(detector.Observe(At(9), isPlaying: true, positionMs, Counters(0, 0)));
        Assert.False(detector.Observe(At(12), isPlaying: true, positionMs, Counters(72, 750_000)));
    }

    // Reporting once is what lets the caller start recovery on the tick that fired: a second report on
    // the very next observation would drive a second teardown into the first one.
    [Fact]
    public void AFreezeIsReportedOnce_AndNeedsAnotherFullThresholdToReportAgain()
    {
        var detector = PlayingVideo(6, out var positionMs, out var counters);

        Assert.False(detector.Observe(At(9), isPlaying: true, positionMs, counters));
        Assert.False(detector.Observe(At(12), isPlaying: true, positionMs, counters));
        Assert.True(detector.Observe(At(15), isPlaying: true, positionMs, counters));

        Assert.False(detector.Observe(At(18), isPlaying: true, positionMs, counters));
        Assert.False(detector.Observe(At(21), isPlaying: true, positionMs, counters));
        Assert.True(detector.Observe(At(24), isPlaying: true, positionMs, counters));
    }
}
