using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0223's Settings surface for the screen and the camera: the monitor, the camera and the one sound each may carry,
/// and the explicit start of each. Nothing here reads a device until the permission is on, and nothing starts a capture
/// except a click on a start button.
/// </summary>
public partial class SettingsWindow
{
    private static readonly string[] CompanionNames = ["none", "mic", "system-audio"];
    private bool _videoOffered;

    private void PopulateBroadcastVideo()
    {
        // The same boundary as the microphone list: the devices of this PC are enumerated only for the person's
        // choice, and only once broadcasting is allowed (SP-0217 constraint 7).
        if (!Broadcast.Settings.Allowed)
        {
            return;
        }

        HandlerBoundary.Run(nameof(PopulateBroadcastVideo), async () =>
        {
            var available = await Broadcast.VideoAvailableAsync();
            _videoOffered = available;
            BroadcastVideoGroup.Visibility = Visibility.Visible;
            BroadcastVideoUnavailableText.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            BroadcastVideoGrid.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            BroadcastScreenPrivacyText.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            if (available)
            {
                var (monitors, cameras) = await Task.Run(ReadVideoDevices);
                FillMonitorChoices(monitors);
                FillCameraChoices(cameras);
                FillCompanionChoices();
            }

            ShowBroadcasting();
        });
    }

    /// <summary>A device list that cannot be read is an empty one: the start button then says the device is missing.</summary>
    private static (IReadOnlyList<WindowsMonitor>, IReadOnlyList<WindowsCameraDevice>) ReadVideoDevices()
    {
        IReadOnlyList<WindowsMonitor> monitors = [];
        IReadOnlyList<WindowsCameraDevice> cameras = [];
        try { monitors = WindowsScreenCapture.Monitors(); }
        catch (System.ComponentModel.Win32Exception) { }
        try { cameras = WindowsCameraCapture.Devices(); }
        catch (System.Runtime.InteropServices.COMException) { }
        return (monitors, cameras);
    }

    private void FillMonitorChoices(IReadOnlyList<WindowsMonitor> monitors)
    {
        var chosen = Broadcast.Settings.MonitorDeviceId;
        var index = monitors.ToList().FindIndex(monitor => monitor.Id == chosen);
        _showingBroadcast = true;
        BroadcastMonitorBox.ItemsSource = monitors.Count == 0
            ? new[] { new VideoDeviceChoice(null, LocalizationService.Get("BroadcastNoDevices")) }
            : monitors.Select(monitor => new VideoDeviceChoice(monitor.Id, monitor.Name)).ToArray();
        BroadcastMonitorBox.SelectedIndex = Math.Max(0, index);
        BroadcastMonitorBox.IsEnabled = monitors.Count > 0;
        _showingBroadcast = false;
        if (chosen is not null && index < 0 && monitors.Count > 0)
        {
            // S15-3: the stored monitor is gone and the list shows the first one in its place, with no selection change to
            // say so (and none to re-pick when one monitor is left). Store what is shown, through the path a pick takes, so
            // Start resolves the same monitor; the guard on the stored value leaves a newer pick alone. An empty list is a
            // failed or empty enumeration and keeps the stored id.
            var shown = monitors[0].Id;
            HandlerBoundary.Run(nameof(FillMonitorChoices), () => Broadcast.UpdateSettingsAsync(settings =>
                settings.MonitorDeviceId == chosen ? settings with { MonitorDeviceId = shown } : settings));
        }
    }

    private void FillCameraChoices(IReadOnlyList<WindowsCameraDevice> cameras)
    {
        var chosen = Broadcast.Settings.CameraDeviceId;
        var index = cameras.ToList().FindIndex(camera => camera.Id == chosen);
        _showingBroadcast = true;
        BroadcastCameraBox.ItemsSource = cameras.Count == 0
            ? new[] { new VideoDeviceChoice(null, LocalizationService.Get("BroadcastNoDevices")) }
            : cameras.Select(camera => new VideoDeviceChoice(camera.Id, string.IsNullOrWhiteSpace(camera.Name) ? camera.Id : camera.Name)).ToArray();
        BroadcastCameraBox.SelectedIndex = Math.Max(0, index);
        BroadcastCameraBox.IsEnabled = cameras.Count > 0;
        _showingBroadcast = false;
        if (chosen is not null && index < 0 && cameras.Count > 0)
        {
            // S15-3: as for the monitor - store the camera the list shows in place of the one that is gone.
            var shown = cameras[0].Id;
            HandlerBoundary.Run(nameof(FillCameraChoices), () => Broadcast.UpdateSettingsAsync(settings =>
                settings.CameraDeviceId == chosen ? settings with { CameraDeviceId = shown } : settings));
        }
    }

    private void FillCompanionChoices()
    {
        var names = new[]
        {
            LocalizationService.Get("BroadcastAudioNone"),
            LocalizationService.Get("BroadcastAudioMicrophone"),
            LocalizationService.Get("BroadcastAudioSystem")
        };
        _showingBroadcast = true;
        BroadcastScreenAudioBox.ItemsSource = names;
        BroadcastCameraAudioBox.ItemsSource = names;
        BroadcastScreenAudioBox.SelectedIndex = Math.Max(0, Array.IndexOf(CompanionNames, Broadcast.Settings.ScreenAudioSource));
        BroadcastCameraAudioBox.SelectedIndex = Math.Max(0, Array.IndexOf(CompanionNames, Broadcast.Settings.CameraAudioSource));
        _showingBroadcast = false;
    }

    /// <summary>Called by <see cref="ShowBroadcasting"/> for the parts that follow the producer's state.</summary>
    private void ShowBroadcastVideoState(IReadOnlyCollection<BroadcastSource> running, bool guestOnly)
    {
        var free = running.Count < BroadcastSources.MaximumSimultaneous;
        var offered = _videoOffered && !guestOnly;
        BroadcastScreenButton.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        BroadcastCameraButton.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        BroadcastScreenButton.IsEnabled = free && !running.Contains(BroadcastSource.Screen);
        BroadcastCameraButton.IsEnabled = free && !running.Contains(BroadcastSource.Camera);
        BroadcastOwnOutputText.Visibility = WindowsAudioCapture.OwnOutputExclusionAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BroadcastMonitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingBroadcast || BroadcastMonitorBox.SelectedItem is not VideoDeviceChoice { Id: { } id })
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastMonitor_Changed), () =>
            Broadcast.UpdateSettingsAsync(settings => settings with { MonitorDeviceId = id }));
    }

    private void BroadcastCamera_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingBroadcast || BroadcastCameraBox.SelectedItem is not VideoDeviceChoice { Id: { } id })
        {
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastCamera_Changed), () =>
            Broadcast.UpdateSettingsAsync(settings => settings with { CameraDeviceId = id }));
    }

    private void BroadcastVideoAudio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingBroadcast || BroadcastScreenAudioBox.SelectedIndex < 0 || BroadcastCameraAudioBox.SelectedIndex < 0)
        {
            return;
        }

        var screen = CompanionNames[BroadcastScreenAudioBox.SelectedIndex];
        var camera = CompanionNames[BroadcastCameraAudioBox.SelectedIndex];
        HandlerBoundary.Run(nameof(BroadcastVideoAudio_Changed), () =>
            Broadcast.UpdateSettingsAsync(settings => settings with { ScreenAudioSource = screen, CameraAudioSource = camera }));
    }

    private void BroadcastScreenStart_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(BroadcastScreenStart_Click), () => Broadcast.StartAsync(BroadcastSource.Screen));

    private void BroadcastCameraStart_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(BroadcastCameraStart_Click), () => Broadcast.StartAsync(BroadcastSource.Camera));
}

/// <summary>One monitor or camera of the Settings choice; a null id is the "none found" placeholder.</summary>
internal sealed record VideoDeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}
