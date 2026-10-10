using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace StreamsPlayer.App;

/// <summary>
/// Closes the modal message box the cast prompt shows, and only that one. A Yes/No box has no close button
/// and ignores WM_CLOSE, so the only way to take it down from code is to end its dialog with the answer it
/// already defaults to (No).
/// </summary>
/// <remarks>
/// <para>MessageBox.Show blocks in a nested message pump and never returns its handle, so the box is
/// identified instead: a snapshot of the dialogs the owner already shows is taken on the UI thread right
/// before Show, and the prompt is the one new visible system dialog of that owner whose caption is the
/// prompt's own (the caption key is used by no other box). Anything that is not exactly one such window -
/// another box on top, none left, two alike - is left alone: the caller is released by the token regardless,
/// and an answer to a box that stays open is discarded because nobody awaits it any more.</para>
/// </remarks>
internal static class PromptDismissal
{
    private const uint GetWindowOwner = 4;
    private const string SystemDialogClass = "#32770";
    private const int AnswerNo = 7;

    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);

    /// <summary>The system dialogs the owner shows right now; call it immediately before showing the prompt.</summary>
    internal static IReadOnlySet<IntPtr> Snapshot(Window owner)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        return ownerHandle == IntPtr.Zero ? new HashSet<IntPtr>() : new HashSet<IntPtr>(DialogsOwnedBy(ownerHandle));
    }

    internal static void Close(Window owner, string caption, IReadOnlySet<IntPtr> existing)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle == IntPtr.Zero)
        {
            return;
        }

        var candidates = DialogsOwnedBy(ownerHandle)
            .Where(dialog => !existing.Contains(dialog) && string.Equals(CaptionOf(dialog, caption.Length), caption, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 1)
        {
            EndDialog(candidates[0], new IntPtr(AnswerNo));
        }
    }

    private static List<IntPtr> DialogsOwnedBy(IntPtr ownerHandle)
    {
        var found = new List<IntPtr>();
        var thread = GetWindowThreadProcessId(ownerHandle, out _);
        if (thread == 0)
        {
            return found;
        }

        // A message box is a top-level window of the thread that showed it, owned by the window it was shown
        // for; the class is the system dialog class a MessageBox is created with.
        EnumThreadWindows(thread, (window, _) =>
        {
            if (GetWindow(window, GetWindowOwner) == ownerHandle && IsWindowVisible(window)
                && string.Equals(ClassOf(window), SystemDialogClass, StringComparison.Ordinal))
            {
                found.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(SystemDialogClass.Length + 2);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    // One character of room beyond the expected caption, so a longer caption reads as different instead of
    // being cut to the expected one.
    private static string CaptionOf(IntPtr window, int expectedLength)
    {
        var text = new StringBuilder(expectedLength + 2);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint thread, EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDialog(IntPtr dialog, IntPtr result);
}
