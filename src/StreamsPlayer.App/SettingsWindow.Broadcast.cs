using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0217's Settings surface: the permission (off by default), the start-with-application setting,
/// the LAN listening opt-in and port, the quality settings, the listener cap and the link password.
/// SP-0252: starting, watching and stopping a source moved to the broadcast window, which this page links to.
/// Nothing here starts a capture: the two-step consent is what the permission gates, and every start is a click there.
/// </summary>
public partial class SettingsWindow
{
    private BroadcastProducerService Broadcast => ((App)Application.Current).Broadcast;
    private bool _showingBroadcast;

    private void InitializeBroadcasting()
    {
        PopulateBroadcastQuality();
        PopulateBroadcastLink();
        Broadcast.Changed += BroadcastChanged;
        Closed += (_, _) => Broadcast.Changed -= BroadcastChanged;
        ShowBroadcasting();
    }

    private void BroadcastChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(ShowBroadcasting);
        }
    }

    private void PopulateBroadcastQuality()
    {
        _showingBroadcast = true;
        BroadcastSampleRateBox.ItemsSource = new[] { "44100", "48000" };
        BroadcastMicChannelsBox.ItemsSource = new[]
        {
            LocalizationService.Get("BroadcastMono"), LocalizationService.Get("BroadcastStereo")
        };
        BroadcastSoundChannelsBox.ItemsSource = new[]
        {
            LocalizationService.Get("BroadcastMono"), LocalizationService.Get("BroadcastStereo")
        };
        BroadcastBitrateBox.ItemsSource = new[] { "96", "128", "160", "192" };
        _showingBroadcast = false;
    }

    private void ShowBroadcasting()
    {
        _showingBroadcast = true;
        var guestOnly = Broadcast.IsGuestOnly;
        BroadcastAllowedCheckBox.IsChecked = Broadcast.Settings.Allowed;
        // SP-0223 requirement 4: a guest session is offered no source at all, so nothing below the permission shows.
        BroadcastControls.Visibility = Broadcast.Settings.Allowed && !guestOnly ? Visibility.Visible : Visibility.Collapsed;
        BroadcastStartWithAppCheckBox.IsChecked = Broadcast.Settings.StartWithApplication;
        BroadcastLanCheckBox.IsChecked = Broadcast.Settings.LanListening;
        BroadcastLanPortBox.Text = Broadcast.Settings.LanPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BroadcastCapBox.Text = Broadcast.Settings.MaxListeners.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BroadcastSampleRateBox.SelectedItem = Broadcast.Settings.SampleRate == 48000 ? "48000" : "44100";
        BroadcastMicChannelsBox.SelectedIndex = Broadcast.Settings.MicrophoneChannels == 2 ? 1 : 0;
        BroadcastSoundChannelsBox.SelectedIndex = Broadcast.Settings.SystemSoundChannels == 2 ? 1 : 0;
        BroadcastBitrateBox.SelectedItem = Array.IndexOf(new[] { "96", "128", "160", "192" },
            Broadcast.Settings.BitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)) >= 0
            ? Broadcast.Settings.BitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "128";
        UpdateBroadcastGuestNote();
        ShowBroadcastLinkState(Broadcast.Running);
        _showingBroadcast = false;
    }

    /// <summary>
    /// A PC that is on a server only as a guest never produces (item AO, SP-0221); the note says why no source is offered.
    /// </summary>
    private void UpdateBroadcastGuestNote() =>
        BroadcastGuestNoteText.Visibility = Broadcast.IsGuestOnly ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// APP-BEHAVIOUR rule 5 and APP-SETTINGS section 9 (SP-0247): a value control whose change ends a broadcast on
    /// the air, disconnects its listeners or revokes the link password asks first, with No as the default answer.
    /// With nothing on the air the change costs nothing and is applied at once, like every other value here.
    /// </summary>
    private bool ConfirmLiveBroadcastChange(string promptKey) =>
        Broadcast.Running.Count == 0
        || MessageBox.Show(this, LocalizationService.Get(promptKey), LocalizationService.Get("BroadcastTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void BroadcastAllowed_Click(object sender, RoutedEventArgs e)
    {
        if (_showingBroadcast)
        {
            return;
        }

        if (BroadcastAllowedCheckBox.IsChecked != true && !ConfirmLiveBroadcastChange("BroadcastConfirmStopAll"))
        {
            ShowBroadcasting();
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastAllowed_Click), async () =>
        {
                await Broadcast.SetAllowedAsync(BroadcastAllowedCheckBox.IsChecked == true);
        });
    }

    private void BroadcastStartWithApp_Click(object sender, RoutedEventArgs e)
    {
        if (_showingBroadcast)
        {
            return;
        }

        // S15-2: the change delegate may run off the UI thread, after the producer's semaphore wait, and after
        // ShowBroadcasting has reset the box; the control is read here, on the UI thread, and only the value is captured.
        var startWithApplication = BroadcastStartWithAppCheckBox.IsChecked == true;
        HandlerBoundary.Run(nameof(BroadcastStartWithApp_Click), async () =>
        {
                await Broadcast.UpdateSettingsAsync(settings => settings with
                {
                    StartWithApplication = startWithApplication
                });
        });
    }

    private void BroadcastLan_Click(object sender, RoutedEventArgs e)
    {
        if (_showingBroadcast)
        {
            return;
        }

        // S15-2: read on the UI thread, as in BroadcastStartWithApp_Click.
        var lanListening = BroadcastLanCheckBox.IsChecked == true;
        if (!lanListening && !ConfirmLiveBroadcastChange("BroadcastConfirmLanOff"))
        {
            ShowBroadcasting();
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastLan_Click), async () =>
        {
                await Broadcast.UpdateSettingsAsync(settings => settings with
                {
                    LanListening = lanListening
                });
        });
    }

    private void BroadcastLanPort_LostFocus(object sender, RoutedEventArgs e) => CommitBroadcastLanPort();

    private void BroadcastLanPort_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitBroadcastLanPort();
        }
    }

    /// <summary>
    /// SP-0246 S15-5: a new port stops the broadcasts that are running, so it is applied when the edit is finished and not
    /// on every keystroke: focus leaves the box, Enter, or the window closes with the box still focused (closing from the
    /// title bar moves no focus, so LostFocus alone would drop the edit).
    /// </summary>
    internal void CommitBroadcastLanPort()
    {
        if (_showingBroadcast || !int.TryParse(BroadcastLanPortBox.Text, out var port) || port is < 1 or > 65535
            || port == Broadcast.Settings.LanPort)
        {
            return;
        }

        if (!ConfirmLiveBroadcastChange("BroadcastConfirmStopAll"))
        {
            ShowBroadcasting();
            return;
        }

        HandlerBoundary.Run(nameof(CommitBroadcastLanPort), async () =>
        {
                await Broadcast.UpdateSettingsAsync(settings => settings with { LanPort = port });
        });
    }

    private void BroadcastCap_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showingBroadcast || !int.TryParse(BroadcastCapBox.Text, out var cap) || cap is < 0 or > 64)
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastCap_TextChanged), async () =>
        {
                await Broadcast.UpdateSettingsAsync(settings => settings with { MaxListeners = cap });
        });
    }

    private void BroadcastQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingBroadcast)
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastQuality_Changed), async () =>
        {
                var sampleRate = BroadcastSampleRateBox.SelectedItem as string == "48000" ? 48000 : 44100;
                var micChannels = BroadcastMicChannelsBox.SelectedIndex == 1 ? 2 : 1;
                var soundChannels = BroadcastSoundChannelsBox.SelectedIndex == 1 ? 2 : 1;
                var bitrate = int.TryParse(BroadcastBitrateBox.SelectedItem as string, out var parsed) && parsed is >= 32 and <= 320 ? parsed : 128;
                await Broadcast.UpdateSettingsAsync(settings => settings with
                {
                    SampleRate = sampleRate,
                    MicrophoneChannels = micChannels,
                    SystemSoundChannels = soundChannels,
                    BitrateKbps = bitrate
                });
        });
    }

    private void BroadcastOpenWindow_Click(object sender, RoutedEventArgs e) => _mainWindow.RequestBroadcastWindowAfterSettings(this);
}
