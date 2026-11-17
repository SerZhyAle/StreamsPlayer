using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0271, <c>APP-COMMANDS</c> rule 9: what a second process prints, and the <c>--result</c> file it writes. This is the
/// product's command output - read by the script that called it - and not a log; nothing else in the application
/// writes to a console.
/// </summary>
/// <remarks>
/// The executable is a window-subsystem one and has no console of its own. It writes to the standard output it was
/// given when a caller redirected one, and otherwise to the console of the process that started it, when there is
/// one. With neither, nothing is printed: the exit code and the <c>--result</c> file are the two channels a caller can
/// always rely on.
/// </remarks>
internal static class CommandConsole
{
    private const int AttachParentProcess = -1;
    private const int StandardOutputHandle = -11;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static bool _channelChecked;

    /// <summary>One line on standard output.</summary>
    internal static void Line(string text) => Emit(Utf8.GetBytes(text + Environment.NewLine), error: false);

    /// <summary>One complaint on standard error.</summary>
    internal static void Complaint(string text) => Emit(Utf8.GetBytes(text + Environment.NewLine), error: true);

    /// <summary>Bytes exactly as given - the manifest of <c>commands --json</c>.</summary>
    internal static void Raw(ReadOnlySpan<byte> bytes) => Emit(bytes, error: false);

    /// <summary>
    /// Rule 9, for every process that answers a command: the message, or the response line with <c>--json</c>, on standard
    /// output; <c>--result</c> holds the data of a finished command, whole or not at all. Returns the exit code the caller
    /// gets - the command's own, or <see cref="AppCommandExit.Io"/> when the result file could not be written. The rule
    /// itself is <see cref="AppCommandOutput"/> in Core (SP-0275), shared with the console companion; this class supplies
    /// the one thing that differs, where a line of this window-subsystem executable goes.
    /// </summary>
    internal static int Report(AppCommandResponse response, bool json, string? resultPath) =>
        AppCommandOutput.Report(response, json, resultPath, Line, Complaint);

    private static void Emit(ReadOnlySpan<byte> bytes, bool error)
    {
        EnsureChannel();
        try
        {
            using var stream = error ? Console.OpenStandardError() : Console.OpenStandardOutput();
            stream.Write(bytes);
            stream.Flush();
        }
        catch (IOException)
        {
            // The caller closed its end; the exit code still says what happened.
        }
    }

    private static void EnsureChannel()
    {
        if (_channelChecked)
        {
            return;
        }

        _channelChecked = true;
        var handle = GetStdHandle(StandardOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            AttachConsole(AttachParentProcess);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);
}
