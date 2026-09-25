using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0118: <c>APP-ACTIVATION</c> rule 5 - bringing a window to the front when the request came from
/// another process, which Windows otherwise answers with a flashing taskbar button.
/// </summary>
/// <remarks>
/// The later copy calls <see cref="AllowAnyProcessToTakeForeground"/> before forwarding, which is
/// normally enough on its own. When it is not, the running copy briefly attaches its input queue to the
/// foreground window's thread - but only after that window has answered a 50 ms responsiveness probe,
/// because attaching to a hung thread would hang this UI thread with it.
/// </remarks>
internal static class ForegroundActivation
{
    private const int AsfwAny = -1;
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ResponsivenessProbeMilliseconds = 50;

    /// <summary>Called by a later copy, which the user just started and which therefore may give focus away.</summary>
    internal static void AllowAnyProcessToTakeForeground() => AllowSetForegroundWindow(AsfwAny);

    /// <summary>
    /// Brings <paramref name="window"/> to the foreground; the result names the path taken and whether
    /// Windows granted it, for the diagnostic log.
    /// </summary>
    internal static string Bring(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return "no-window";
        }

        var foreground = GetForegroundWindow();
        if (foreground == handle)
        {
            return "already";
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 &&
                       foregroundThread != currentThread &&
                       IsResponsive(foreground) &&
                       AttachThreadInput(foregroundThread, currentThread, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foregroundThread, currentThread, false);
            }
        }

        return $"{(attached ? "attached" : "direct")}:{(GetForegroundWindow() == handle ? "granted" : "refused")}";
    }

    private static bool IsResponsive(IntPtr window) =>
        SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, ResponsivenessProbeMilliseconds, out _) != IntPtr.Zero;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
