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

    /// <summary>
    /// SP-0084: places a remembered rectangle that may no longer make sense - the size is corrected as
    /// well as the position, which is what separates this from <see cref="Clamp"/>.
    /// </summary>
    /// <remarks>
    /// A deliberately separate method rather than a wider <see cref="Clamp"/>. The compact panel is
    /// fixed-size and its rule is that the size is never touched; a remembered player window is the
    /// opposite case, because the monitor it was sized on may be gone, smaller, or scaled differently.
    /// Widening the shared method would have quietly changed the panel's behaviour to buy this one.
    /// <para>
    /// The size is capped to the work area first and only then raised to the window's minimum, so a
    /// minimum larger than the screen still yields a usable window rather than an unreachable one - it
    /// simply cannot satisfy both edges, and <see cref="ClampAxis"/> pins it to the origin exactly as it
    /// does for the panel. A degenerate work area returns the rectangle untouched, for the same reason
    /// as <see cref="Clamp"/>: a caller that could not read the monitor must not be told to move a
    /// window to nowhere.
    /// </para>
    /// </remarks>
    public static ScreenRect Fit(ScreenRect window, ScreenRect workArea, double minWidth, double minHeight)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return window;
        }

        var sized = window with
        {
            Width = FitLength(window.Width, workArea.Width, minWidth),
            Height = FitLength(window.Height, workArea.Height, minHeight)
        };

        return Clamp(sized, workArea);
    }

    private static double FitLength(double length, double areaLength, double minimum) =>
        Math.Max(Math.Min(length, areaLength), minimum);

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
