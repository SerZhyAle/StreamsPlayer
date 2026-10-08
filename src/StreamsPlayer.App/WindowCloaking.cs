using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StreamsPlayer.App;

/// <summary>
/// Whether the shell has cloaked a window - the state a window is in while it lives on another virtual desktop
/// (<c>WAVE-PARTICLES</c> rule 10, as widened in 0.13: a surface nobody can see draws nothing). The window stays
/// "visible" to WPF and is not minimized, so neither check the backdrop already makes can tell. Windows sends no
/// WPF-visible event when the state changes, so the backdrop asks on each tick of its own timer.
/// </summary>
internal static class WindowCloaking
{
    private const int DwmwaCloaked = 14;

    public static bool IsCloaked(Window? window)
    {
        if (window is null)
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // no DWM to ask: treat the window as visible, as before this check existed
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
