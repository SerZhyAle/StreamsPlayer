using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0073 criteria 1-4: the rule behind the player's "what is on air" line, driven as a sequence of
/// readings with no window, no network and no media backend. The sources this exists for - one that
/// rewrites the field every few seconds, one that sends 4 KB of padding, one that sends a bidi override -
/// cannot be summoned on demand, which is why the decision lives in a type that can be exercised here.
/// </summary>
public sealed class NowPlayingTrackerTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly double Hold = NowPlayingTracker.MinimumHold.TotalSeconds;

    [Fact]
    public void SaysNothingBeforeTheStreamHasAnnouncedAnything()
    {
        // Criterion 2: a stream that reports nothing must leave the player exactly as it was.
        var tracker = new NowPlayingTracker();

        Assert.False(tracker.Observe(At(0), null));
        Assert.False(tracker.Observe(At(2), "   "));
        Assert.Null(tracker.Text);
    }

    [Fact]
    public void ShowsTheFirstAnnouncementImmediately()
    {
        // Criterion 1: nothing is gained by making the viewer wait for the first line - the hold exists
        // to stop churn, and there is nothing yet to churn against.
        var tracker = new NowPlayingTracker();

        Assert.True(tracker.Observe(At(0), "Ludovico Einaudi - Nuvole Bianche"));
        Assert.Equal("Ludovico Einaudi - Nuvole Bianche", tracker.Text);
    }

    [Fact]
    public void ReportsNoChangeWhileTheStreamRepeatsItself()
    {
        // The common case: the field is re-read every couple of seconds and almost always says the same
        // thing. The caller repaints and logs on the return value, so this has to be false.
        var tracker = new NowPlayingTracker();
        tracker.Observe(At(0), "Station ID");

        Assert.False(tracker.Observe(At(2), "Station ID"));
        Assert.False(tracker.Observe(At(Hold + 30), "Station ID"));
    }

    [Fact]
    public void RefusesASecondLineInsideTheHold()
    {
        // Criterion 4, the "changing every second" half: a source rewriting the field per segment must
        // not twitch the panel.
        var tracker = new NowPlayingTracker();
        tracker.Observe(At(0), "First");

        Assert.False(tracker.Observe(At(1), "Second"));
        Assert.False(tracker.Observe(At(Hold - 0.5), "Third"));
        Assert.Equal("First", tracker.Text);
    }

    [Fact]
    public void TakesTheCurrentReadingOnceTheHoldHasPassed()
    {
        // The other half of the rule, and the reason a refused candidate is deliberately not remembered:
        // what appears next is what the stream says *now*, not the value that was rejected earlier.
        var tracker = new NowPlayingTracker();
        tracker.Observe(At(0), "First");
        tracker.Observe(At(1), "Second");

        Assert.True(tracker.Observe(At(Hold), "Third"));
        Assert.Equal("Third", tracker.Text);
    }

    [Fact]
    public void KeepsTheLineWhenTheStreamGoesQuiet()
    {
        // Decision 3: every re-open builds a media whose metadata is empty until its first block lands,
        // so a blank reading must not erase - or the line would blink through every reconnect.
        var tracker = new NowPlayingTracker();
        tracker.Observe(At(0), "Ludovico Einaudi - Nuvole Bianche");

        Assert.False(tracker.Observe(At(Hold + 5), null));
        Assert.False(tracker.Observe(At(Hold + 7), ""));
        Assert.Equal("Ludovico Einaudi - Nuvole Bianche", tracker.Text);
    }

    [Fact]
    public void ClearErasesImmediatelyAndReportsIt()
    {
        // Criterion 3: a stop or a terminal break must not leave the previous broadcast's text behind,
        // and it must not wait out the hold to do it.
        var tracker = new NowPlayingTracker();
        tracker.Observe(At(0), "Ludovico Einaudi - Nuvole Bianche");

        Assert.True(tracker.Clear());
        Assert.Null(tracker.Text);
        Assert.False(tracker.Clear());
    }

    [Fact]
    public void ShowsTheNextBroadcastImmediatelyAfterAClear()
    {
        // A cleared line carries no residual hold: the hold protects a line that is on screen, and after
        // a clear there is none.
        var tracker = new NowPlayingTracker();
        tracker.Observe(At(0), "Previous channel");
        tracker.Clear();

        Assert.True(tracker.Observe(At(1), "New channel"));
        Assert.Equal("New channel", tracker.Text);
    }

    [Fact]
    public void BoundsAnAbsurdlyLongAnnouncement()
    {
        // Criterion 4, the "very long" half. The panel trims what it draws, but the rule must not hand it
        // an unbounded string in the first place.
        var tracker = new NowPlayingTracker();

        Assert.True(tracker.Observe(At(0), new string('x', 4096)));
        Assert.Equal(NowPlayingTracker.MaxLength, tracker.Text!.Length);
    }

    [Fact]
    public void TreatsAnAnnouncementOfOnlyControlCharactersAsSilence()
    {
        // Criterion 4, the "service characters" half: nothing legible means no line, not an empty one.
        var tracker = new NowPlayingTracker();

        Assert.False(tracker.Observe(At(0), "\r\n\t\0"));
        Assert.Null(tracker.Text);
    }

    [Fact]
    public void StripsABidirectionalOverrideButKeepsTheWords()
    {
        // Decision 6: char.IsControl does not cover U+202E, so without the strip a broadcaster could
        // reverse the reading order of the whole panel line - layout damage from untrusted text, which
        // criterion 4 forbids.
        // Built from the code point rather than pasted in: the character is invisible, so a literal one
        // here would be unreviewable in a diff and lost to the first re-encoding of this file.
        const char rightToLeftOverride = (char)0x202E;
        var tracker = new NowPlayingTracker();

        Assert.True(tracker.Observe(At(0), $"Radio{rightToLeftOverride} Paradise"));
        Assert.Equal("Radio Paradise", tracker.Text);
    }

    [Fact]
    public void FoldsNewlinesAndCollapsesWhitespace()
    {
        // A multi-line announcement must become one line, because it is rendered into one.
        var tracker = new NowPlayingTracker();

        Assert.True(tracker.Observe(At(0), "  Artist \r\n\t  Title  "));
        Assert.Equal("Artist Title", tracker.Text);
    }
}
