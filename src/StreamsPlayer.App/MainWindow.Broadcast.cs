using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0217's indicator in the main window: always visible while any source is live, naming the live
/// sources, with a stop for each on its menu. The producer's own events drive it; nothing here starts
/// or captures anything.
/// </summary>
public partial class MainWindow
{
    private BroadcastProducerService Broadcast => ((App)Application.Current).Broadcast;

    internal void InitializeBroadcastIndicator()
    {
        Broadcast.Changed += BroadcastProducerChanged;
        Exchange.DirectoryChanged += BroadcastDirectoryForMenu;
        Closed += (_, _) =>
        {
            Broadcast.Changed -= BroadcastProducerChanged;
            Exchange.DirectoryChanged -= BroadcastDirectoryForMenu;
        };
        RefreshBroadcastIndicator();
    }

    private void BroadcastProducerChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(RefreshBroadcastIndicator);
        }
    }

    private void BroadcastDirectoryForMenu()
    {
        // The cast menu reads the account's receivers; a directory change while it is open is rare, and
        // the indicator line itself does not depend on it.
    }

    private void RefreshBroadcastIndicator()
    {
        var running = Broadcast.Running;
        BroadcastLiveButton.Visibility = running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_compactPanel is { } closed)
        {
            closed.BroadcastAnchor.Visibility = running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (running.Count == 0)
        {
            return;
        }

        var names = running.Select(Broadcast.SourceTitle).OrderBy(name => name, StringComparer.CurrentCulture);
        var description = LocalizationService.Format("BroadcastIndicatorLive", string.Join(", ", names));
        BroadcastLiveButton.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(BroadcastLiveButton, description);
        // The compact panel shows the same indicator, with the same menu behind it.
        if (_compactPanel is { } panel)
        {
            panel.BroadcastAnchor.Visibility = Visibility.Visible;
        }
    }

    internal static string SourceTitle(BroadcastSource source) => source switch
    {
        BroadcastSource.Microphone => LocalizationService.Get("BroadcastMicTitle"),
        BroadcastSource.SystemSound => LocalizationService.Get("BroadcastSystemSoundTitle"),
        BroadcastSource.Screen => LocalizationService.Get("BroadcastScreenTitle"),
        BroadcastSource.Camera => LocalizationService.Get("BroadcastCameraTitle"),
        _ => LocalizationService.Get("BroadcastMicTitle")
    };

    private async void BroadcastLiveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The menu is rebuilt on each open: the live set, the receivers and their support verdicts are
            // what is true now, not what was true when the indicator was last refreshed. SP-0222: the
            // receivers are asked for here, once, because the directory is not held while nobody looks.
            var menu = await BroadcastLiveMenuBuilder.BuildAsync(Broadcast, Exchange, this);
            BroadcastLiveButton.ContextMenu = menu;
            menu.PlacementTarget = BroadcastLiveButton;
            menu.IsOpen = true;
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(BroadcastLiveButton_Click), exception);
        }
    }
}
