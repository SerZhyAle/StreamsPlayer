using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0080 risk "a lost panel": the compact panel carries the only playback controls a collapsed
/// application has, so a position it cannot be seen at is a loss of control, not a cosmetic problem.
/// </summary>
public sealed class ScreenPlacementTests
{
    // A single 1920x1080 monitor with a taskbar along the bottom.
    private static readonly ScreenRect WorkArea = new(0, 0, 1920, 1032);

    [Fact]
    public void LeavesAWindowThatIsAlreadyInsideAlone()
    {
        var window = new ScreenRect(400, 300, 460, 118);

        Assert.Equal(window, ScreenPlacement.Clamp(window, WorkArea));
    }

    [Fact]
    public void PullsAWindowDraggedPastTheRightEdgeBack()
    {
        var clamped = ScreenPlacement.Clamp(new ScreenRect(1900, 300, 460, 118), WorkArea);

        Assert.Equal(1460, clamped.Left);
        Assert.Equal(1920, clamped.Right);
        Assert.Equal(300, clamped.Top);
    }

    [Fact]
    public void PullsAWindowDraggedBelowTheWorkAreaBack()
    {
        var clamped = ScreenPlacement.Clamp(new ScreenRect(400, 1030, 460, 118), WorkArea);

        Assert.Equal(914, clamped.Top);
        Assert.Equal(1032, clamped.Bottom);
    }

    [Fact]
    public void PushesAWindowDraggedOffTheTopLeftBackToTheOrigin()
    {
        var clamped = ScreenPlacement.Clamp(new ScreenRect(-300, -80, 460, 118), WorkArea);

        Assert.Equal(0, clamped.Left);
        Assert.Equal(0, clamped.Top);
    }

    /// <summary>
    /// The switched-off-monitor case. The panel stood on a second screen to the right; that screen is
    /// gone and the surviving one is the work area, so the panel is nowhere near it.
    /// </summary>
    [Fact]
    public void BringsAWindowLeftOnADisconnectedMonitorFullyInside()
    {
        var clamped = ScreenPlacement.Clamp(new ScreenRect(3200, 900, 460, 118), WorkArea);

        Assert.True(clamped.Left >= WorkArea.Left);
        Assert.True(clamped.Top >= WorkArea.Top);
        Assert.True(clamped.Right <= WorkArea.Right);
        Assert.True(clamped.Bottom <= WorkArea.Bottom);
    }

    /// <summary>
    /// No position satisfies both edges, so the rule has to pick one rather than oscillate. It picks
    /// the origin: the panel's controls sit at its leading edge.
    /// </summary>
    [Fact]
    public void PinsAWindowLargerThanTheWorkAreaToTheOrigin()
    {
        var tiny = new ScreenRect(100, 100, 300, 200);

        var clamped = ScreenPlacement.Clamp(new ScreenRect(-50, -50, 460, 300), tiny);

        Assert.Equal(100, clamped.Left);
        Assert.Equal(100, clamped.Top);
    }

    [Fact]
    public void LeavesTheWindowAloneWhenTheWorkAreaCouldNotBeRead()
    {
        var window = new ScreenRect(-300, -80, 460, 118);

        Assert.Equal(window, ScreenPlacement.Clamp(window, new ScreenRect(0, 0, 0, 0)));
    }

    [Fact]
    public void ClampsAgainstAWorkAreaThatDoesNotStartAtTheOrigin()
    {
        // A left-hand secondary monitor puts the primary's work area at a positive offset, and a
        // taskbar on the left puts its own at one too.
        var secondary = new ScreenRect(-1920, 0, 1920, 1032);

        var clamped = ScreenPlacement.Clamp(new ScreenRect(-2100, 500, 460, 118), secondary);

        Assert.Equal(-1920, clamped.Left);
    }
}
