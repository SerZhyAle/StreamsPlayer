using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// The two ways a saved channel is started from outside the application - a desktop shortcut and a command
/// line - offered from the channel's own menu. SP-0109 moved the command here from the Settings window,
/// where it acted on whichever row happened to be selected and ran behind a Cancel that could not undo it.
/// </summary>
public partial class MainWindow
{
    private void CreateDesktopShortcutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChannelRow row)
        {
            return;
        }

        try
        {
            var path = StreamShortcutService.CreateDesktopShortcut(row.Channel);
            SetStatus("DesktopShortcutCreated", path);
            ShowIdOnlyLaunchNotice(row.Channel, "CreateDesktopShortcut");
        }
        // The shell writes the file through COM, so a desktop that rejects the path - too long, read-only,
        // a name already held by a directory - arrives as an IOException rather than a COMException.
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            SetStatus("DesktopShortcutFailed");
        }
    }

    private void CopyLaunchCommandMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChannelRow row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(StreamShortcutService.BuildLaunchCommand(row.Channel));
            SetStatus("LaunchCommandCopied");
            ShowIdOnlyLaunchNotice(row.Channel, "MenuCopyLaunchCommand");
        }
        catch (COMException)
        {
            // Another process owns the clipboard.
            SetStatus("LaunchCommandCopyFailed");
        }
    }

    private void ShowIdOnlyLaunchNotice(StreamChannel channel, string titleKey)
    {
        if (!StreamLaunchArguments.CarriesAddress(channel))
        {
            MessageBox.Show(DialogOwner, LocalizationService.Get("LaunchIdOnlyNotice"),
                LocalizationService.Get(titleKey), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
