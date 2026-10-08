using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StreamsPlayer.App;

/// <summary>
/// Makes a window's native title bar follow the application theme where the platform lets it
/// (<c>APP-SETTINGS</c> rule 9). The caption belongs to the operating system, so this asks it for its dark
/// variant and nothing more - no repainting of a surface that is not ours, which is the cost
/// <c>APP-STYLE</c> section 8 declines. Under a high-contrast theme the window is handed back to the system
/// scheme, whatever the application mode.
/// </summary>
internal static class WindowTitleBar
{
    // 20 is the documented value (Windows 10 20H1 and later); 19 is what builds 1809-1903 read.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>
    /// Applies the current theme now and again after every palette change, until the window closes. The
    /// handle exists only after the source is initialised, so a window not yet shown is applied then.
    /// </summary>
    public static void Follow(Window window)
    {
        void Apply() => ApplyTo(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply();
        }
        else
        {
            window.SourceInitialized += (_, _) => Apply();
        }

        ThemeService.PaletteChanged += Apply;
        window.Closed += (_, _) => ThemeService.PaletteChanged -= Apply;
    }

    private static void ApplyTo(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = ThemeService.IsDark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DWM to ask: the system caption stays as it was, which is the compromise APP-STYLE names.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
