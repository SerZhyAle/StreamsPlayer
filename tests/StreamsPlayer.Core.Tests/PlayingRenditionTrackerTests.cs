using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0077 criteria 1-3: which readings become a log line, which stay silent, and how a reading that
/// never came is spelled. Driven as a sequence of readings with no engine at all - the interesting
/// states (a rendition change without a re-open, an engine that answers nothing) are hard to provoke on a
/// live stream and impossible to provoke on demand, which is why the rule lives here.
/// </summary>
public sealed class PlayingRenditionTrackerTests
{
    private static readonly VideoRendition Low = new(426, 240);
    private static readonly VideoRendition High = new(1024, 576);

    private static RenditionObservation Reported(RenditionObservation? observation)
    {
        Assert.NotNull(observation);
        return observation.Value;
    }

    // Criterion 1: the first thing the log has to answer is what was on screen at all.
    [Fact]
    public void FirstKnownReadingOfALegOpensIt()
    {
        var tracker = new PlayingRenditionTracker();

        var opened = Reported(tracker.Observe(1, Low));

        Assert.Equal(RenditionCause.Opened, opened.Cause);
        Assert.Null(opened.From);
        Assert.Equal(Low, opened.To);
        Assert.Equal(Low, tracker.Shown);
    }

    // The steady state, which is most of a session: a line per tick would bury the ones that matter.
    [Fact]
    public void AnUnchangedReadingSaysNothing()
    {
        var tracker = new PlayingRenditionTracker();
        tracker.Observe(1, Low);

        Assert.Null(tracker.Observe(1, Low));
        Assert.Null(tracker.Observe(1, Low));
    }

    // Criterion 1 again, and the reason the ticket exists: this happens with no re-open behind it.
    [Fact]
    public void AChangeInsideOneLegIsASwitch()
    {
        var tracker = new PlayingRenditionTracker();
        tracker.Observe(1, Low);

        var switched = Reported(tracker.Observe(1, High));

        Assert.Equal(RenditionCause.Switched, switched.Cause);
        Assert.Equal(Low, switched.From);
        Assert.Equal(High, switched.To);
    }

    // Criterion 2, the load-bearing half: the same resolution across a re-open is not a switch, and the
    // previous leg is not offered as the value it switched "from".
    [Fact]
    public void ANewLegOpensEvenWhenTheResolutionIsUnchanged()
    {
        var tracker = new PlayingRenditionTracker();
        tracker.Observe(1, Low);

        var reopened = Reported(tracker.Observe(2, Low));

        Assert.Equal(RenditionCause.Opened, reopened.Cause);
        Assert.Null(reopened.From);
        Assert.Equal(Low, reopened.To);
    }

    // Criterion 2, the other half: a different resolution across a re-open is still a re-open. Without
    // this the log would blame an in-flight switch for what a reconnect did.
    [Fact]
    public void ANewLegWithADifferentResolutionIsStillAnOpen()
    {
        var tracker = new PlayingRenditionTracker();
        tracker.Observe(1, High);

        var reopened = Reported(tracker.Observe(2, Low));

        Assert.Equal(RenditionCause.Opened, reopened.Cause);
        Assert.Null(reopened.From);
    }

    // Criterion 3: an engine that says nothing has to be visible as such, exactly once.
    [Fact]
    public void SilenceIsReportedOncePerLeg()
    {
        var tracker = new PlayingRenditionTracker();

        var silence = Reported(tracker.Observe(1, null));

        Assert.Equal(RenditionCause.Opened, silence.Cause);
        Assert.Null(silence.To);
        Assert.Null(tracker.Shown);
        Assert.Null(tracker.Observe(1, null));
        Assert.Null(tracker.Observe(1, null));
    }

    [Fact]
    public void EachLegGetsItsOwnSilenceLine()
    {
        var tracker = new PlayingRenditionTracker();
        tracker.Observe(1, null);

        Assert.NotNull(tracker.Observe(2, null));
    }

    // A value arriving after the silence line completes the opening; nothing switched, because this leg
    // never had a rendition to switch away from.
    [Fact]
    public void AValueAfterSilenceStillOpensTheLeg()
    {
        var tracker = new PlayingRenditionTracker();
        tracker.Observe(1, null);

        var opened = Reported(tracker.Observe(1, High));

        Assert.Equal(RenditionCause.Opened, opened.Cause);
        Assert.Null(opened.From);
        Assert.Equal(High, opened.To);
    }

    // The vout is briefly rebuilt during a stall and answers nothing. That is not a rendition change, and
    // it must not erase what the session is known to have shown.
    [Fact]
    public void SilenceAfterAKnownRenditionChangesNothing()
    {
        var tracker = new PlayingRenditionTracker();
        tracker.Observe(1, High);

        Assert.Null(tracker.Observe(1, null));
        Assert.Equal(High, tracker.Shown);
        Assert.Null(tracker.Observe(1, High)); // and the same value coming back is still not news
    }

    // Both engines answer with zeroes before they have a picture. A "0x0" rung in the log would read as a
    // rendition rather than as the absence of one.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1024, 0)]
    [InlineData(0, 576)]
    [InlineData(-1, -1)]
    public void AnUnderDeclaredReadingIsNoReading(int width, int height)
    {
        var tracker = new PlayingRenditionTracker();

        var silence = Reported(tracker.Observe(1, new VideoRendition(width, height)));

        Assert.Null(silence.To);
        Assert.Null(tracker.Shown);
    }

    // Criterion 3: nothing may read the absence of a reading as "the ceiling was respected".
    [Fact]
    public void NoReadingIsNotCompliance()
    {
        Assert.Equal(CeilingCompliance.Unknown, PlayingRenditionTracker.Compare(null, new StreamQualityRung(836_000, 848, 480)));
    }

    [Fact]
    public void NoCeilingIsItsOwnAnswer()
    {
        Assert.Equal(CeilingCompliance.NoCeiling, PlayingRenditionTracker.Compare(High, null));
    }

    [Theory]
    [InlineData(848, 480, CeilingCompliance.Within)]  // exactly the ceiling: a rung the engine may play
    [InlineData(640, 360, CeilingCompliance.Within)]
    [InlineData(1280, 480, CeilingCompliance.Above)]  // over in width only
    [InlineData(848, 720, CeilingCompliance.Above)]   // over in height only
    [InlineData(1920, 1080, CeilingCompliance.Above)]
    public void ARenditionIsMeasuredAgainstTheCeilingInBothDimensions(int width, int height, CeilingCompliance expected)
    {
        var ceiling = new StreamQualityRung(836_000, 848, 480);

        Assert.Equal(expected, PlayingRenditionTracker.Compare(new VideoRendition(width, height), ceiling));
    }

    [Fact]
    public void ARenditionIsSpelledTheSameEverywhereItIsLogged()
    {
        Assert.Equal("1024x576", High.Describe());
    }
}
