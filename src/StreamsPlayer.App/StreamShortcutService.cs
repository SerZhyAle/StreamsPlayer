using System.IO;
using System.Runtime.InteropServices;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal static class StreamShortcutService
{
    public static string BuildLaunchCommand(Guid channelId) => $"\"{LaunchExecutablePath}\" {LaunchArguments(channelId)}";

    public static string CreateDesktopShortcut(StreamChannel channel)
    {
        var path = DesktopShortcutName.PathFor(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            StreamTitleFormatter.Display(channel.Title));
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException();
        dynamic shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException();
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = LaunchExecutablePath;
        shortcut.Arguments = LaunchArguments(channel.Id);
        shortcut.WorkingDirectory = AppContext.BaseDirectory;
        shortcut.IconLocation = LaunchExecutablePath;
        shortcut.Save();
        return path;
    }

    private static string LaunchExecutablePath
    {
        get
        {
            var localExecutable = Path.Combine(AppContext.BaseDirectory, "StreamsPlayer.exe");
            return File.Exists(localExecutable) ? localExecutable : Environment.ProcessPath ?? localExecutable;
        }
    }

    private static string LaunchArguments(Guid channelId) => $"--id \"{channelId:D}\"";
}
