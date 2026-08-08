using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0072 criteria 1-3: the rule behind the caption drawn over a black player, driven as a sequence of
/// events with no window, no network and no media backend. The situation it exists for - a source that
/// blacks out for three to eighteen seconds at a time - cannot be provoked on demand, which is why the
/// decision sits in a type that can be exercised here rather than in the player.
/// </summary>
public sealed class PlaybackInterruptionTrackerTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly double Delay = PlaybackInterruptionTracker.AppearDelay.TotalSeconds;

    /// <summary>A tracker that has already played, which is where every "and then it broke" test starts.</summary>
    private static PlaybackInterruptionTracker Live()
    {
        var tracker = new PlaybackInterruptionTracker();
        tracker.NotifyInterrupted(At(0), PlaybackInterruptionKind.Connecting);
        tracker.NotifyLive();
        return tracker;
    }

    [Fact]
    public void SaysNothingWhileTheStreamIsPlaying()
    {
        // Criterion 2, second half: during steady viewing the caption is never on screen for a moment.
        var tracker = Live();

        Assert.Equal(PlaybackInterruptionKind.None, tracker.Evaluate(At(600)).Kind);
    }

    [Fact]
    public void SaysNothingAboutABlackoutShorterThanTheAppearDelay()
    {
        // The flicker risk, stated as a fact: a sub-delay dip must not produce a caption that is shown
        // and taken away again, which is more irritating than the silence the ticket set out to end.
        var tracker = Live();
        tracker.NotifyInterrupted(At(10), PlaybackInterruptionKind.SignalLost);

        Assert.Equal(PlaybackInterruptionKind.None, tracker.Evaluate(At(10 + (Delay / 2))).Kind);

        tracker.NotifyLive();

        Assert.Equal(PlaybackInterruptionKind.None, tracker.Evaluate(At(10 + Delay + 5)).Kind);
    }

    [Fact]
    public void NamesTheCauseOnceTheBlackoutHasLasted()
    {
        // Criterion 1: while the picture is gone the screen carries the reason, with nothing else needed
        // to make it appear - no mouse, no visible control panel.
        var tracker = Live();
        tracker.NotifyInterrupted(At(10), PlaybackInterruptionKind.SignalLost);

        Assert.Equal(PlaybackInterruptionKind.SignalLost, tracker.Evaluate(At(10 + Delay)).Kind);
    }

    [Fact]
    public void CarriesTheAttemptNumberOfARecovery()
    {
        // Criterion 3: a reconnect is distinguishable from the other causes *and* says which attempt it is.
        var tracker = Live();
        tracker.NotifyInterrupted(At(10), PlaybackInterruptionKind.Reconnecting, attempt: 2, budget: 5);

        var notice = tracker.Evaluate(At(10 + Delay));

        Assert.Equal(new PlaybackInterruptionNotice(PlaybackInterruptionKind.Reconnecting, 2, 5), notice);
    }

    [Fact]
    public void ChangingTheCauseMidBlackoutReplacesTheTextWithoutRestartingTheDelay()
    {
        // One interruption, not three. A recovery runs probe -> attempt 1 -> attempt 2 while the screen
        // stays black throughout; restarting the delay per leg would take the caption away and bring it
        // back in the middle of a single blackout, which is the flicker the ticket forbids.
        var tracker = Live();
        tracker.NotifyInterrupted(At(10), PlaybackInterruptionKind.SignalLost);
        tracker.NotifyInterrupted(At(10 + (Delay / 2)), PlaybackInterruptionKind.Reconnecting, attempt: 1, budget: 5);

        var atDelay = tracker.Evaluate(At(10 + Delay));

        Assert.Equal(PlaybackInterruptionKind.Reconnecting, atDelay.Kind);
        Assert.Equal(1, atDelay.Attempt);

        tracker.NotifyInterrupted(At(10 + Delay + 3), PlaybackInterruptionKind.Reconnecting, attempt: 2, budget: 5);

        Assert.Equal(2, tracker.Evaluate(At(10 + Delay + 3)).Attempt);
    }

    [Fact]
    public void ClearsTheCaptionTheInstantThePictureReturns()
    {
        // Criterion 2, first half. Deliberately no minimum visible time: a caption held over a picture
        // that is already back would contradict the criterion it is meant to serve.
        var tracker = Live();
        tracker.NotifyInterrupted(At(10), PlaybackInterruptionKind.Reconnecting, attempt: 1, budget: 5);
        Assert.Equal(PlaybackInterruptionKind.Reconnecting, tracker.Evaluate(At(20)).Kind);

        tracker.NotifyLive();

        Assert.Equal(PlaybackInterruptionKind.None, tracker.Evaluate(At(20)).Kind);
    }

    [Fact]
    public void MakesASecondBlackoutEarnTheDelayAgain()
    {
        // Without this the flapping source - six re-opens in four minutes - would get a caption that
        // snaps on instantly from the second dip onward, turning the delay into a one-time courtesy.
        var tracker = Live();
        tracker.NotifyInterrupted(At(10), PlaybackInterruptionKind.SignalLost);
        Assert.Equal(PlaybackInterruptionKind.SignalLost, tracker.Evaluate(At(20)).Kind);
        tracker.NotifyLive();

        tracker.NotifyInterrupted(At(30), PlaybackInterruptionKind.SignalLost);

        Assert.Equal(PlaybackInterruptionKind.None, tracker.Evaluate(At(30 + (Delay / 2))).Kind);
        Assert.Equal(PlaybackInterruptionKind.SignalLost, tracker.Evaluate(At(30 + Delay)).Kind);
    }

    [Fact]
    public void RefusesToBeToldThePictureIsBackThroughTheInterruptionPath()
    {
        // "The picture is back" has one spelling, and it is NotifyLive. Two ways to say it is two states
        // to keep in step.
        var tracker = Live();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.NotifyInterrupted(At(10), PlaybackInterruptionKind.None));
    }
}
