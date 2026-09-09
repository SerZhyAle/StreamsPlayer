using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// Which monitor a rectangle belongs to, and how much of it a window may use.
/// </summary>
/// <remarks>
/// Extracted from the compact panel (SP-0080) when the player window came to need the same answer
/// (SP-0084). Two windows placed by two copies of this P/Invoke would be two chances to disagree about
/// where a monitor is, and the interesting cases - a screen switched off, a second one at a different
/// scale - are exactly the ones nobody re-tests on the copy.
/// </remarks>
internal static class MonitorWorkArea
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>A window's current rectangle, falling back to its requested size before it has been measured.</summary>
    internal static ScreenRect Placement(Window window) =>
        new(window.Left,
            window.Top,
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            window.ActualHeight > 0 ? window.ActualHeight : window.Height);

    /// <summary>
    /// The work area of the monitor nearest the given rectangle, in device-independent units.
    /// </summary>
    /// <remarks>
    /// <c>MONITOR_DEFAULTTONEAREST</c> is the whole answer to a switched-off monitor: it returns the
    /// nearest surviving one, and the caller's clamp then brings the window onto it. Falls back to
    /// <see cref="SystemParameters.WorkArea"/> - the primary monitor - when the window has no
    /// presentation source yet or the call fails. That is a worse answer than the real one, never a
    /// wrong one: the fallback is still a real work area.
    /// <para>
    /// <paramref name="dpiSource"/> is the window the rectangle belongs to, which is not always the
    /// window being placed: two windows can sit on monitors with different scaling, and converting one's
    /// position through the other's transform would land it off by exactly that ratio. Before a window
    /// is shown it has no transform of its own, so a caller placing one has to lend another's and correct
    /// the result once the real one exists.
    /// </para>
    /// </remarks>
    internal static ScreenRect Around(ScreenRect rectangle, Visual dpiSource)
    {
        var fallback = SystemParameters.WorkArea;
        var primary = new ScreenRect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        if (PresentationSource.FromVisual(dpiSource)?.CompositionTarget is not { } target)
        {
            return primary;
        }

        var topLeft = target.TransformToDevice.Transform(new Point(rectangle.Left, rectangle.Top));
        var bottomRight = target.TransformToDevice.Transform(new Point(rectangle.Right, rectangle.Bottom));
        var device = new NativeRect
        {
            Left = (int)Math.Floor(topLeft.X),
            Top = (int)Math.Floor(topLeft.Y),
            Right = (int)Math.Ceiling(bottomRight.X),
            Bottom = (int)Math.Ceiling(bottomRight.Y)
        };

        var monitor = MonitorFromRect(ref device, MonitorDefaultToNearest);
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return primary;
        }

        var workTopLeft = target.TransformFromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var workBottomRight = target.TransformFromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new ScreenRect(
            workTopLeft.X,
            workTopLeft.Y,
            workBottomRight.X - workTopLeft.X,
            workBottomRight.Y - workTopLeft.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);
}
