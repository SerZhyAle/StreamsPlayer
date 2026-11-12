using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>One monitor, camera or microphone of a choice; a null id is the Windows default or the "none found" placeholder.</summary>
internal sealed record BroadcastDeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The per-source choices of the broadcast window (SP-0252 requirement 2): the monitor and the sound that goes with the
/// screen, the camera and its sound, the microphone. They are the producer's settings, written through it, and they
/// are read at the start of a source - a card whose source is on the air does not offer them. Devices are listed only
/// once broadcasting is allowed (SP-0217 constraint 7).
/// </summary>
public partial class BroadcastWindow
{
    private static readonly string[] CompanionNames = ["none", "mic", "system-audio"];
    private bool _populating;
    private bool _devicesRequested;
    private bool _videoKnown;
    private bool _videoOffered;
    private IReadOnlyList<WindowsMonitor> _monitors = [];
    private IReadOnlyList<WindowsCameraDevice> _cameras = [];
    private IReadOnlyList<WindowsAudioDevice> _microphones = [];

    private void InitializeChoices()
    {
        ScreenCard.SetChoices(LocalizationService.Get("BroadcastMonitor"), LocalizationService.Get("BroadcastScreenAudio"));
        CameraCard.SetChoices(LocalizationService.Get("BroadcastCamera"), LocalizationService.Get("BroadcastCameraAudio"));
        MicrophoneCard.SetChoices(LocalizationService.Get("BroadcastMicDevice"), null);
        SystemSoundCard.SetChoices(null, null);
        // The handlers are attached once; the lists are filled by FillChoices.
        ScreenCard.FirstChoice.SelectionChanged -= Monitor_Changed;
        ScreenCard.FirstChoice.SelectionChanged += Monitor_Changed;
        ScreenCard.SecondChoice.SelectionChanged -= Companion_Changed;
        ScreenCard.SecondChoice.SelectionChanged += Companion_Changed;
        CameraCard.FirstChoice.SelectionChanged -= Camera_Changed;
        CameraCard.FirstChoice.SelectionChanged += Camera_Changed;
        CameraCard.SecondChoice.SelectionChanged -= Companion_Changed;
        CameraCard.SecondChoice.SelectionChanged += Companion_Changed;
        MicrophoneCard.FirstChoice.SelectionChanged -= Microphone_Changed;
        MicrophoneCard.FirstChoice.SelectionChanged += Microphone_Changed;
        FillChoices();
    }

    /// <summary>Reads the devices of this PC once, the first time the window finds broadcasting allowed.</summary>
    private void LoadDevicesOnce()
    {
        if (_devicesRequested)
        {
            return;
        }

        _devicesRequested = true;
        HandlerBoundary.Run(nameof(LoadDevicesOnce), async () =>
        {
            _videoOffered = await _producer.VideoAvailableAsync();
            _videoKnown = true;
            (_monitors, _cameras, _microphones) = await Task.Run(ReadDevices);
            FillChoices();
            Refresh();
        });
    }

    /// <summary>A device list that cannot be read is an empty one: the start then says the device is missing.</summary>
    private (IReadOnlyList<WindowsMonitor>, IReadOnlyList<WindowsCameraDevice>, IReadOnlyList<WindowsAudioDevice>) ReadDevices()
    {
        IReadOnlyList<WindowsMonitor> monitors = [];
        IReadOnlyList<WindowsCameraDevice> cameras = [];
        IReadOnlyList<WindowsAudioDevice> microphones = [];
        if (_videoOffered)
        {
            try { monitors = WindowsScreenCapture.Monitors(); }
            catch (System.ComponentModel.Win32Exception) { }
            try { cameras = WindowsCameraCapture.Devices(); }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        try { microphones = WindowsAudioCapture.ListCaptureDevices(); }
        catch (System.Runtime.InteropServices.COMException) { }
        return (monitors, cameras, microphones);
    }

    private void FillChoices()
    {
        var settings = _producer.Settings;
        _populating = true;
        try
        {
            Fill(ScreenCard.FirstChoice, Devices(_monitors.Select(monitor => new BroadcastDeviceChoice(monitor.Id, monitor.Name)), settings.MonitorDeviceId));
            Fill(CameraCard.FirstChoice, Devices(_cameras.Select(camera =>
                new BroadcastDeviceChoice(camera.Id, string.IsNullOrWhiteSpace(camera.Name) ? camera.Id : camera.Name)), settings.CameraDeviceId));
            FillMicrophones(settings.MicrophoneDeviceId);
            var companions = new[]
            {
                LocalizationService.Get("BroadcastAudioNone"),
                LocalizationService.Get("BroadcastAudioMicrophone"),
                LocalizationService.Get("BroadcastAudioSystem")
            };
            ScreenCard.SecondChoice.ItemsSource = companions;
            CameraCard.SecondChoice.ItemsSource = companions;
            ScreenCard.SecondChoice.SelectedIndex = Math.Max(0, Array.IndexOf(CompanionNames, settings.ScreenAudioSource));
            CameraCard.SecondChoice.SelectedIndex = Math.Max(0, Array.IndexOf(CompanionNames, settings.CameraAudioSource));
        }
        finally
        {
            _populating = false;
        }
    }

    /// <summary>The devices found plus, when the chosen one is gone, a line that says so - never a silent switch to another.</summary>
    private static (IReadOnlyList<BroadcastDeviceChoice> Choices, int Index, bool Any) Devices(IEnumerable<BroadcastDeviceChoice> found, string? chosen)
    {
        var choices = found.ToList();
        var any = choices.Count > 0;
        if (chosen is not null && !choices.Any(choice => choice.Id == chosen))
        {
            choices.Add(new BroadcastDeviceChoice(chosen, LocalizationService.Get("BroadcastDeviceMissing")));
        }

        if (choices.Count == 0)
        {
            choices.Add(new BroadcastDeviceChoice(null, LocalizationService.Get("BroadcastNoDevices")));
        }

        return (choices, Math.Max(0, choices.FindIndex(choice => choice.Id == chosen)), any);
    }

    private static void Fill(ComboBox box, (IReadOnlyList<BroadcastDeviceChoice> Choices, int Index, bool Any) devices)
    {
        box.ItemsSource = devices.Choices;
        box.SelectedIndex = devices.Index;
        box.IsEnabled = devices.Any;
    }

    private void FillMicrophones(string? chosen)
    {
        var items = new List<BroadcastDeviceChoice> { new(null, LocalizationService.Get("BroadcastDefaultDevice")) };
        items.AddRange(_microphones.Select(device => new BroadcastDeviceChoice(device.Id, string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name)));
        if (chosen is not null && !items.Any(choice => choice.Id == chosen))
        {
            items.Add(new BroadcastDeviceChoice(chosen, LocalizationService.Get("BroadcastDeviceMissing")));
        }

        MicrophoneCard.FirstChoice.ItemsSource = items;
        MicrophoneCard.FirstChoice.SelectedIndex = chosen is null ? 0 : Math.Max(0, items.FindIndex(choice => choice.Id == chosen));
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_populating && ScreenCard.FirstChoice.SelectedItem is BroadcastDeviceChoice { Id: { } id })
        {
            HandlerBoundary.Run(nameof(Monitor_Changed), () => _producer.UpdateSettingsAsync(settings => settings with { MonitorDeviceId = id }));
        }
    }

    private void Camera_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_populating && CameraCard.FirstChoice.SelectedItem is BroadcastDeviceChoice { Id: { } id })
        {
            HandlerBoundary.Run(nameof(Camera_Changed), () => _producer.UpdateSettingsAsync(settings => settings with { CameraDeviceId = id }));
        }
    }

    private void Microphone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_populating && MicrophoneCard.FirstChoice.SelectedItem is BroadcastDeviceChoice choice)
        {
            HandlerBoundary.Run(nameof(Microphone_Changed), () => _producer.UpdateSettingsAsync(settings => settings with { MicrophoneDeviceId = choice.Id }));
        }
    }

    private void Companion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || ScreenCard.SecondChoice.SelectedIndex < 0 || CameraCard.SecondChoice.SelectedIndex < 0)
        {
            return;
        }

        // Read on the interface thread: the change delegate runs after the producer's wait, when the boxes may have been refilled.
        var screen = CompanionNames[ScreenCard.SecondChoice.SelectedIndex];
        var camera = CompanionNames[CameraCard.SecondChoice.SelectedIndex];
        HandlerBoundary.Run(nameof(Companion_Changed),
            () => _producer.UpdateSettingsAsync(settings => settings with { ScreenAudioSource = screen, CameraAudioSource = camera }));
    }
}
