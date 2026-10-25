using System.IO;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0217's Settings surface: the permission (off by default), the start-with-application setting,
/// the LAN listening opt-in and port, the microphone device, the quality settings, the listener cap -
/// and the explicit start of each source. Nothing here starts a capture on its own: every start is a
/// click, and the two-step consent is what the permission gates.
/// </summary>
public partial class SettingsWindow
{
    private BroadcastProducerService Broadcast => ((App)Application.Current).Broadcast;
    private bool _showingBroadcast;

    private void InitializeBroadcasting()
    {
        PopulateBroadcastQuality();
        PopulateBroadcastLink();
        PopulateBroadcastVideo();
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
        BroadcastSampleRateBox.ItemsSource = new[] { "44100", "48000" };
        BroadcastMicChannelsBox.ItemsSource = new[]
        {
            LocalizationService.Get("BroadcastMono"), LocalizationService.Get("BroadcastStereo")
        };
        BroadcastSoundChannelsBox.ItemsSource = new[]
        {
            LocalizationService.Get("BroadcastMono"), LocalizationService.Get("BroadcastStereo")
        };
        BroadcastBitrateBox.ItemsSource = new[] { "96", "128", "192", "256" };
        PopulateBroadcastMicrophones();
    }

    private void PopulateBroadcastMicrophones()
    {
        // With broadcasting not allowed no capture API is ever called (constraint 7): the device list
        // is read only for the Settings choice, and only once the permission is on.
        var previous = Broadcast.Settings.MicrophoneDeviceId;
        var items = new List<object> { new BroadcastMicrophoneChoice(null, LocalizationService.Get("BroadcastDefaultDevice")) };
        if (Broadcast.Settings.Allowed)
        {
            foreach (var device in WindowsAudioCapture.ListCaptureDevices())
            {
                items.Add(new BroadcastMicrophoneChoice(device.Id, string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name));
            }
        }

        _showingBroadcast = true;
        BroadcastMicDeviceBox.ItemsSource = items;
        BroadcastMicDeviceBox.SelectedIndex = previous is null ? 0
            : Math.Max(0, items.FindIndex(choice => choice is BroadcastMicrophoneChoice { Id: { } id } && id == previous));
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
        BroadcastBitrateBox.SelectedItem = Array.IndexOf(new[] { "96", "128", "192", "256" },
            Broadcast.Settings.BitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)) >= 0
            ? Broadcast.Settings.BitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "128";
        UpdateBroadcastGuestNote();
        var running = Broadcast.Running;
        BroadcastMicButton.IsEnabled = running.Count < BroadcastSources.MaximumSimultaneous
            && !running.Contains(BroadcastSource.Microphone);
        BroadcastSoundButton.IsEnabled = running.Count < BroadcastSources.MaximumSimultaneous
            && !running.Contains(BroadcastSource.SystemSound);
        BroadcastStopButton.IsEnabled = running.Count > 0;
        ShowBroadcastVideoState(running, guestOnly);
        ShowBroadcastLinkState(running);
        BroadcastStatusText.Text = Broadcast.OutcomeKey is { } key ? LocalizationService.Get(key) : "";
        _showingBroadcast = false;
    }

    /// <summary>
    /// A PC that is on a server only as a guest never produces (item AO, SP-0221); the note says why no source is offered.
    /// </summary>
    private void UpdateBroadcastGuestNote() =>
        BroadcastGuestNoteText.Visibility = Broadcast.IsGuestOnly ? Visibility.Visible : Visibility.Collapsed;

    private void BroadcastAllowed_Click(object sender, RoutedEventArgs e)
    {
        if (_showingBroadcast)
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastAllowed_Click), async () =>
        {
                await Broadcast.SetAllowedAsync(BroadcastAllowedCheckBox.IsChecked == true);
                if (Broadcast.Settings.Allowed)
                {
                    PopulateBroadcastMicrophones();
                    PopulateBroadcastVideo();
                }
        });
    }

    private void BroadcastStartWithApp_Click(object sender, RoutedEventArgs e)
    {
        if (_showingBroadcast)
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastStartWithApp_Click), async () =>
        {
                await Broadcast.UpdateSettingsAsync(settings => settings with
                {
                    StartWithApplication = BroadcastStartWithAppCheckBox.IsChecked == true
                });
        });
    }

    private void BroadcastLan_Click(object sender, RoutedEventArgs e)
    {
        if (_showingBroadcast)
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastLan_Click), async () =>
        {
                await Broadcast.UpdateSettingsAsync(settings => settings with
                {
                    LanListening = BroadcastLanCheckBox.IsChecked == true
                });
        });
    }

    private void BroadcastLanPort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showingBroadcast || !int.TryParse(BroadcastLanPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastLanPort_TextChanged), async () =>
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

    private void BroadcastMicDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingBroadcast || BroadcastMicDeviceBox.SelectedItem is not BroadcastMicrophoneChoice choice)
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastMicDevice_Changed), async () =>
        {
                await Broadcast.UpdateSettingsAsync(settings => settings with { MicrophoneDeviceId = choice.Id });
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

    private void BroadcastMicStart_Click(object sender, RoutedEventArgs e)
    {
        HandlerBoundary.Run(nameof(BroadcastMicStart_Click), async () =>
        {
                await Broadcast.StartAsync(BroadcastSource.Microphone);
        });
    }

    private void BroadcastSoundStart_Click(object sender, RoutedEventArgs e)
    {
        HandlerBoundary.Run(nameof(BroadcastSoundStart_Click), async () =>
        {
                await Broadcast.StartAsync(BroadcastSource.SystemSound);
        });
    }

    private void BroadcastStopAll_Click(object sender, RoutedEventArgs e)
    {
        HandlerBoundary.Run(nameof(BroadcastStopAll_Click), async () =>
        {
                await Broadcast.StopAllAsync();
        });
    }
}

/// <summary>One microphone of the Settings choice; a null id is the Windows default.</summary>
internal sealed record BroadcastMicrophoneChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}
