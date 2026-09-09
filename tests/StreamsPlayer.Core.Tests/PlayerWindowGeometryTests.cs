using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0084. The ticket names its own main risk: the vanished monitor is the ordinary case, not a rare
/// one, and the placement rule therefore has to be provable without a second screen to unplug. These are
/// that proof - every case below is arithmetic over a work area the test supplies.
/// </summary>
public class PlayerWindowGeometryTests
{
    private static readonly ScreenRect WorkArea = new(0, 0, 1920, 1040);

    private const double MinWidth = 640;
    private const double MinHeight = 400;

    private static ScreenRect Fit(ScreenRect window, ScreenRect? workArea = null) =>
        ScreenPlacement.Fit(window, workArea ?? WorkArea, MinWidth, MinHeight);

    [Fact]
    public void Fit_LeavesARectangleThatAlreadyFits()
    {
        var window = new ScreenRect(300, 200, 1120, 720);

        Assert.Equal(window, Fit(window));
    }

    [Fact]
    public void Fit_BringsBackAWindowRememberedOnAMonitorThatIsGone()
    {
        // The classic laptop case: the rectangle was recorded on a second screen to the right, and this
        // launch has only the built-in one. The App resolves the nearest surviving monitor; the rule's
        // job is to put the window wholly inside it.
        var window = new ScreenRect(2600, 400, 1120, 720);

        var fitted = Fit(window);

        Assert.Equal(800, fitted.Left);
        // Pulled up as well: the remembered top plus the height overran this work area's bottom edge,
        // and a title bar below the taskbar is a window the user cannot move.
        Assert.Equal(320, fitted.Top);
        Assert.Equal(1120, fitted.Width);
        Assert.Equal(720, fitted.Height);
    }

    [Fact]
    public void Fit_BringsBackAWindowRememberedOnAMonitorAboveAndLeftOfThisOne()
    {
        var window = new ScreenRect(-1500, -900, 1120, 720);

        var fitted = Fit(window);

        Assert.Equal(0, fitted.Left);
        Assert.Equal(0, fitted.Top);
    }

    [Fact]
    public void Fit_ShrinksAWindowRememberedOnABiggerScreen()
    {
        // Criterion 4: the resolution changed under the memory. A 4K rectangle restored on a 1080p panel
        // must not open larger than the work area.
        var window = new ScreenRect(0, 0, 3840, 2160);

        var fitted = Fit(window);

        Assert.Equal(1920, fitted.Width);
        Assert.Equal(1040, fitted.Height);
        Assert.Equal(0, fitted.Left);
        Assert.Equal(0, fitted.Top);
    }

    [Fact]
    public void Fit_RaisesAWindowRememberedSmallerThanTheWindowsOwnMinimum()
    {
        // Criterion 4's other half: a scale change can shrink a remembered rectangle below the size the
        // window can actually be, and a window opened tiny is one the user has to fix by hand.
        var window = new ScreenRect(100, 100, 120, 90);

        var fitted = Fit(window);

        Assert.Equal(MinWidth, fitted.Width);
        Assert.Equal(MinHeight, fitted.Height);
    }

    [Fact]
    public void Fit_PrefersTheMinimumWhenTheWorkAreaIsSmallerThanIt()
    {
        // Both edges cannot be satisfied. A usable window pinned to the origin beats one too small to
        // use, and pinning to the origin is what keeps the title bar reachable.
        var tiny = new ScreenRect(0, 0, 400, 300);

        var fitted = Fit(new ScreenRect(50, 50, 1120, 720), tiny);

        Assert.Equal(MinWidth, fitted.Width);
        Assert.Equal(MinHeight, fitted.Height);
        Assert.Equal(0, fitted.Left);
        Assert.Equal(0, fitted.Top);
    }

    [Fact]
    public void Fit_LeavesTheWindowAloneWhenTheWorkAreaIsUnreadable()
    {
        // The App could not read the monitor. Moving the window to nowhere would be worse than leaving it.
        var window = new ScreenRect(300, 200, 1120, 720);

        Assert.Equal(window, Fit(window, new ScreenRect(0, 0, 0, 0)));
    }

    [Fact]
    public void Fit_RespectsAWorkAreaThatDoesNotStartAtTheOrigin()
    {
        var window = new ScreenRect(0, 0, 1120, 720);

        var fitted = Fit(window, new ScreenRect(1920, 100, 1920, 1040));

        Assert.Equal(1920, fitted.Left);
        Assert.Equal(100, fitted.Top);
    }

    [Fact]
    public void Fit_DoesNotChangeWhatClampPromisesTheCompactPanel()
    {
        // The compact panel's rule is that its size is never touched. Fit is a separate method precisely
        // so that stays true; this pins the two apart rather than letting a later edit merge them.
        var oversized = new ScreenRect(50, 50, 3840, 2160);

        var clamped = ScreenPlacement.Clamp(oversized, WorkArea);

        Assert.Equal(3840, clamped.Width);
        Assert.Equal(2160, clamped.Height);
    }

    [Theory]
    [InlineData(double.NaN, 100, 1120, 720)]
    [InlineData(100, double.NaN, 1120, 720)]
    [InlineData(100, 100, double.PositiveInfinity, 720)]
    [InlineData(100, 100, 1120, double.NaN)]
    [InlineData(100, 100, 0, 720)]
    [InlineData(100, 100, 1120, -5)]
    public void IsUsable_RefusesAnythingThatIsNotARectangle(double left, double top, double width, double height) =>
        Assert.False(PlayerWindowGeometry.IsUsable(new ScreenRect(left, top, width, height)));

    [Fact]
    public void IsUsable_AcceptsANegativePositionOnAMonitorLeftOfThePrimary() =>
        Assert.True(PlayerWindowGeometry.IsUsable(new ScreenRect(-1920, -200, 1120, 720)));

    [Fact]
    public void Recall_KnowsNothingAboutAChannelNeverOpenedInAWindow() =>
        Assert.Null(PlayerWindowGeometry.Recall([], "https://example.test/stream.m3u8"));

    [Fact]
    public void Recall_MatchesTheSameSourceWrittenWithADifferentCasedHost()
    {
        // Criterion 5 leans on this: a refresh can rewrite the row's URL casing, and the memory has to
        // survive that. The normalized identity is the one the hidden-channel list already uses.
        var entries = PlayerWindowGeometry.Record(
            [], "https://Example.TEST/Stream.m3u8", new ScreenRect(10, 20, 800, 600), DateTimeOffset.UnixEpoch);

        var recalled = PlayerWindowGeometry.Recall(entries, "https://example.test/Stream.m3u8");

        Assert.Equal(new ScreenRect(10, 20, 800, 600), recalled);
    }

    [Fact]
    public void Recall_ReadsAStoredNonRectangleAsNoMemory()
    {
        // The file is hand-editable and outlives builds. A window opened at NaN cannot be recovered by
        // the user, so nonsense has to degrade to the default placement.
        var entries = new[]
        {
            new ChannelWindowGeometry("https://example.test/s.m3u8", DateTimeOffset.UnixEpoch, double.NaN, 0, 800, 600)
        };

        Assert.Null(PlayerWindowGeometry.Recall(entries, "https://example.test/s.m3u8"));
    }

    [Fact]
    public void Record_ReplacesThisChannelsRectangleAndLeavesEveryOtherAlone()
    {
        // Criterion 2 in the small: one channel's window must never be dragged around by another's.
        var entries = PlayerWindowGeometry.Record(
            [], "https://a.test/s", new ScreenRect(0, 0, 800, 600), DateTimeOffset.UnixEpoch);
        entries = PlayerWindowGeometry.Record(
            entries, "https://b.test/s", new ScreenRect(500, 500, 900, 700), DateTimeOffset.UnixEpoch);
        entries = PlayerWindowGeometry.Record(
            entries, "https://a.test/s", new ScreenRect(30, 40, 1000, 800), DateTimeOffset.UnixEpoch);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new ScreenRect(30, 40, 1000, 800), PlayerWindowGeometry.Recall(entries, "https://a.test/s"));
        Assert.Equal(new ScreenRect(500, 500, 900, 700), PlayerWindowGeometry.Recall(entries, "https://b.test/s"));
    }

    [Fact]
    public void Record_RefusesARectangleThatIsNotOne()
    {
        var entries = PlayerWindowGeometry.Record(
            [], "https://a.test/s", new ScreenRect(0, 0, double.NaN, 600), DateTimeOffset.UnixEpoch);

        Assert.Empty(entries);
    }

    [Fact]
    public void Record_RefusesAChannelWithNoUrlToBeKeyedBy()
    {
        var entries = PlayerWindowGeometry.Record(
            [], "   ", new ScreenRect(0, 0, 800, 600), DateTimeOffset.UnixEpoch);

        Assert.Empty(entries);
    }

    [Fact]
    public void Record_KeepsTheListProportionalToTheChannelsActuallyOpened()
    {
        // The growth risk. Records are created by closing a window, so the cap is reached only by a user
        // who has opened that many distinct channels in windows - never by the size of the bank.
        IReadOnlyList<ChannelWindowGeometry> entries = [];
        for (var i = 0; i < PlayerWindowGeometry.MaxChannels + 30; i++)
        {
            entries = PlayerWindowGeometry.Record(
                entries,
                $"https://example.test/{i}",
                new ScreenRect(0, 0, 800, 600),
                DateTimeOffset.UnixEpoch.AddMinutes(i));
        }

        Assert.Equal(PlayerWindowGeometry.MaxChannels, entries.Count);
        // The most recently left window is kept and the longest untouched one is what goes.
        Assert.NotNull(PlayerWindowGeometry.Recall(entries, $"https://example.test/{PlayerWindowGeometry.MaxChannels + 29}"));
        Assert.Null(PlayerWindowGeometry.Recall(entries, "https://example.test/0"));
    }

    [Fact]
    public void Record_DoesNotForgetAChannelJustBecauseTheBankStoppedListingIt()
    {
        // The ticket's decision: absence from the catalog is not authority to clear a window's placement,
        // because the channel can come back. Nothing in this type takes a catalog, which is how that
        // decision is enforced rather than merely intended - this pins it.
        var entries = PlayerWindowGeometry.Record(
            [], "https://gone.test/s", new ScreenRect(7, 8, 900, 700), DateTimeOffset.UnixEpoch);

        var later = PlayerWindowGeometry.Record(
            entries, "https://other.test/s", new ScreenRect(0, 0, 800, 600), DateTimeOffset.UnixEpoch.AddDays(400));

        Assert.Equal(new ScreenRect(7, 8, 900, 700), PlayerWindowGeometry.Recall(later, "https://gone.test/s"));
    }
}
