using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0252: the header's one broadcast control. It opens the broadcast window, and its look follows the producer's own
/// events - neutral while nothing is live, red with the number of live sources while anything is, and a warning mark
/// while the exchange server is unreachable and a broadcast needs it. Nothing here starts or captures anything.
/// </summary>
public partial class MainWindow
{
    private BroadcastProducerService Broadcast => ((App)Application.Current).Broadcast;

    // One broadcast window: a second open brings the first forward (APP-BEHAVIOUR rule 1, as the incoming list does).
    private BroadcastWindow? _broadcastWindow;

    internal void InitializeBroadcastIndicator()
    {
        Broadcast.Changed += BroadcastProducerChanged;
        Closed += (_, _) => Broadcast.Changed -= BroadcastProducerChanged;
        RefreshBroadcastIndicator();
    }

    private void BroadcastProducerChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(RefreshBroadcastIndicator);
        }
    }

    private void RefreshBroadcastIndicator()
    {
        var running = Broadcast.Running;
        var live = running.Count > 0;
        var warning = Broadcast.ServerNeededButUnreachable;

        BroadcastButton.Tag = live ? running.Count.ToString(CultureInfo.CurrentCulture) : string.Empty;
        BroadcastWarningMark.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
        if (!live)
        {
            // Idle, the name and the tooltip are the dictionary's own, so they follow the language without a refresh
            // (this runs before the saved language is applied at start-up).
            BroadcastButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            BroadcastButton.SetResourceReference(FrameworkElement.ToolTipProperty, "BroadcastButtonIdle");
            BroadcastButton.SetResourceReference(AutomationProperties.NameProperty, "BroadcastWindowTitle");
            AutomationProperties.SetHelpText(BroadcastButton, string.Empty);
            if (_compactPanel is { } idlePanel)
            {
                idlePanel.BroadcastAnchor.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var titles = running.Select(Broadcast.SourceTitle).OrderBy(title => title, StringComparer.CurrentCulture);
        var name = LocalizationService.Format("BroadcastIndicatorLive", string.Join(", ", titles));
        BroadcastButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "DangerBrush");
        BroadcastButton.ToolTip = warning ? name + Environment.NewLine + LocalizationService.Get("BroadcastButtonServerWarning") : name;
        // The accessible name carries the state too: colour and a number alone say nothing to a screen reader.
        AutomationProperties.SetName(BroadcastButton, name);
        AutomationProperties.SetHelpText(BroadcastButton, warning ? LocalizationService.Get("BroadcastButtonServerWarning") : string.Empty);
        if (_compactPanel is { } panel)
        {
            // The compact panel keeps a small indicator of its own while anything is live; it opens the same window.
            panel.BroadcastAnchor.Visibility = Visibility.Visible;
            panel.BroadcastAnchor.ToolTip = name;
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

    private void BroadcastButton_Click(object sender, RoutedEventArgs e) => OpenBroadcastWindow();

    /// <summary>Opens the broadcast window, or brings the open one forward; closing it never stops a broadcast.</summary>
    internal void OpenBroadcastWindow(Window? owner = null)
    {
        try
        {
            if (_broadcastWindow is { } open)
            {
                ActivateWindow(open);
                return;
            }

            var window = new BroadcastWindow(this) { Owner = owner ?? DialogOwner };
            _broadcastWindow = window;
            window.Closed += (_, _) => _broadcastWindow = null;
            window.Show();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(OpenBroadcastWindow), exception);
        }
    }
}
