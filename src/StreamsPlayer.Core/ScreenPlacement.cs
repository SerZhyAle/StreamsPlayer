namespace StreamsPlayer.Core;

/// <summary>A window rectangle or a monitor work area, in device-independent units.</summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// SP-0080: the rule that keeps the compact panel reachable. A small always-on-top window can be
/// dragged past an edge, and the monitor it stood on can be switched off while it is there; either way
/// it takes the only volume, stop and sleep-timer controls the listener has with it.
/// </summary>
/// <remarks>
/// Pure arithmetic on purpose. Finding the monitor and its DPI is the App's job; deciding where a
/// rectangle has to move is the part that can be proved without a screen.
/// </remarks>
public static class ScreenPlacement
{
    /// <summary>Moves <paramref name="window"/> the shortest distance that puts it inside <paramref name="workArea"/>.</summary>
    /// <remarks>
    /// The size is never changed - the panel is deliberately fixed-size - so a window larger than the
    /// work area cannot satisfy both edges. It is pinned to the origin, because the controls sit at the
    /// leading edge and a window pinned the other way would hide them.
    /// A degenerate work area returns the window untouched: a caller that could not read the monitor
    /// must not be told to move the panel to nowhere.
    /// </remarks>
    public static ScreenRect Clamp(ScreenRect window, ScreenRect workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return window;
        }

        return window with
        {
            Left = ClampAxis(window.Left, window.Width, workArea.Left, workArea.Width),
            Top = ClampAxis(window.Top, window.Height, workArea.Top, workArea.Height)
        };
    }

    // The pull-back runs first and the push-forward second, so the near edge wins on an axis where both
    // are violated. That ordering is what makes a window dragged off the left edge come back.
    private static double ClampAxis(double start, double length, double areaStart, double areaLength)
    {
        if (length >= areaLength)
        {
            return areaStart;
        }

        var pulled = Math.Min(start, areaStart + areaLength - length);
        return Math.Max(pulled, areaStart);
    }
}
