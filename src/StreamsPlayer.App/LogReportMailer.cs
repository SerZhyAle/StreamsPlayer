using System.ComponentModel;
using System.Diagnostics;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0040/SP-0049: hands a finished log archive to the user's own mail program and, only on request,
/// opens its containing folder.
/// </summary>
/// <remarks>
/// <para>
/// The application never sends anything itself: no SMTP, no upload, no credentials. It opens a prepared
/// message the user reads and sends, or does not.
/// </para>
/// <para>
/// <c>mailto:</c> cannot carry an attachment - the parameter is not part of the scheme and Windows mail
/// clients ignore or reject it - so attaching stays a user gesture. The archive's full path is carried in
/// both the prepared mail and the confirmation; opening its folder is a separate action the user chooses.
/// The alternative, Simple MAPI, would attach automatically but silently has no client on a webmail-only
/// desktop, which is the common case; a support feature must not be the thing that fails.
/// </para>
/// </remarks>
internal static class LogReportMailer
{
    /// <summary>Opens the default mail program with recipient, subject and body prepared.</summary>
    public static bool Compose(string recipient, string subject, string body) =>
        TryStart(new ProcessStartInfo(DiagnosticMailLink.Build(recipient, subject, body)) { UseShellExecute = true });

    /// <summary>Opens the archive's containing folder after the user explicitly asks for it.</summary>
    public static bool OpenFolder(string folder) =>
        TryStart(new ProcessStartInfo(folder) { UseShellExecute = true });

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            // No handler registered for the scheme, or the shell refused the call. The caller turns this
            // into the localized "no mail program" message plus the archive's path, so the user can still
            // send the report by hand.
            return false;
        }
    }
}
