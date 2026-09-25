using System.IO;
using System.Runtime.InteropServices;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal static class StreamShortcutService
{
    /// <summary>The package's execution alias (<c>msix/AppxManifest.xml</c>); it must match the file name.</summary>
    private const string ExecutionAlias = "StreamsPlayer.exe";

    // GetCurrentPackageFullName's answer for a process that has no package identity.
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<bool> IsPackaged = new(DetectPackageIdentity);

    public static string BuildLaunchCommand(StreamChannel channel) =>
        $"\"{LaunchExecutablePath}\" {StreamLaunchArguments.For(channel)}";

    /// <summary>
    /// Writes the channel's shortcut to the desktop and returns its path. SP-0127: a name another
    /// channel's shortcut already holds gets a distinguishing suffix instead of being overwritten; this
    /// channel's own earlier shortcut is rewritten in place.
    /// </summary>
    public static string CreateDesktopShortcut(StreamChannel channel)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException();
        dynamic shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException();
        var path = DesktopShortcutName.FreePathFor(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            StreamTitleFormatter.Display(channel.Title),
            candidate => IsHeldByAnotherChannel(shell, candidate, channel.Id))
            ?? throw new IOException("Every shortcut name for this title is already taken.");
        var executable = LaunchExecutablePath;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = executable;
        shortcut.Arguments = StreamLaunchArguments.For(channel);
        shortcut.WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
        shortcut.IconLocation = executable;
        shortcut.Save();
        return path;
    }

    private static bool IsHeldByAnotherChannel(dynamic shell, string path, Guid channelId)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            // CreateShortcut on an existing file loads it without writing anything.
            dynamic existing = shell.CreateShortcut(path);
            return !StreamLaunchArguments.Names((string?)existing.Arguments, channelId);
        }
        catch (COMException)
        {
            // Not a shortcut the shell can read - certainly not this channel's.
            return true;
        }
    }

    /// <summary>
    /// SP-0127: what a shortcut or a launch command starts. In the packaged build the executable sits in a
    /// versioned install folder the next Store update removes, so a packaged copy points at its execution
    /// alias instead - a stable path in the user's WindowsApps folder that always starts the installed
    /// version. An unpackaged copy points at itself, as before.
    /// </summary>
    private static string LaunchExecutablePath
    {
        get
        {
            if (IsPackaged.Value)
            {
                var alias = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft",
                    "WindowsApps",
                    ExecutionAlias);
                if (File.Exists(alias))
                {
                    return alias;
                }
            }

            var localExecutable = Path.Combine(AppContext.BaseDirectory, "StreamsPlayer.exe");
            return File.Exists(localExecutable) ? localExecutable : Environment.ProcessPath ?? localExecutable;
        }
    }

    private static bool DetectPackageIdentity()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);
}
