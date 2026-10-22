using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0230: opens a local help page in the user's default browser so that the page's <c>#anchor</c> and
/// <c>?theme</c> survive. The shell would open the same file, but converts a <c>file:</c> address to a path first and
/// loses both, which would put every help request at the top of its page. The browser's own registered command
/// (<see cref="BrowserCommand"/>) takes the whole address; when it cannot be read the shell is the fallback, and the
/// page opens at its top.
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>Opens the address and says how: <c>browser</c> (anchor kept) or <c>shell</c> (anchor lost).</summary>
    /// <exception cref="Win32Exception">Neither the browser nor the shell could be started.</exception>
    internal static string Open(string address)
    {
        if (TryStartDefaultBrowser(address))
        {
            return "browser";
        }

        using var process = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        return "shell";
    }

    private static bool TryStartDefaultBrowser(string address)
    {
        try
        {
            var command = BrowserCommand.Build(ReadDefaultBrowserCommand(), address);
            if (command is null)
            {
                return false;
            }

            var start = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(command.Value.Program), command.Value.Arguments)
            {
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or SecurityException or UnauthorizedAccessException or IOException)
        {
            // A browser whose command cannot be read or started is not an error to show: the shell opens the page.
            return false;
        }
    }

    private static string? ReadDefaultBrowserCommand()
    {
        foreach (var scheme in new[] { "https", "http" })
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId)
            {
                continue;
            }

            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue(null) is string template)
            {
                return template;
            }
        }

        return null;
    }
}
