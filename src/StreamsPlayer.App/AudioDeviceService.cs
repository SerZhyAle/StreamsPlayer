using LibVLCSharp.Shared;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal static class AudioOutputSettingsExtensions
{
    public static AudioOutputChannel ToLibVlc(this AudioChannelMode mode) => mode switch
    {
        AudioChannelMode.Left => AudioOutputChannel.Left,
        AudioChannelMode.Right => AudioOutputChannel.Right,
        AudioChannelMode.ReverseStereo => AudioOutputChannel.RStereo,
        AudioChannelMode.DolbySurround => AudioOutputChannel.Dolbys,
        _ => AudioOutputChannel.Stereo
    };
}

internal static class AudioDeviceService
{
    public static IReadOnlyList<UiOption> GetAvailableAudioDevices()
    {
        var list = new List<UiOption>
        {
            new(string.Empty, LocalizationService.Get("AudioDeviceDefault"))
        };

        try
        {
            var libVlc = StandardAudioPlayback.SharedLibVlcInstance;
            var outputs = libVlc.AudioOutputs;
            var outputName = outputs.FirstOrDefault(o => o.Name.Equals("mmdevice", StringComparison.OrdinalIgnoreCase)).Name
                ?? outputs.FirstOrDefault(o => o.Name.Equals("directsound", StringComparison.OrdinalIgnoreCase)).Name
                ?? outputs.FirstOrDefault().Name;

            if (!string.IsNullOrEmpty(outputName))
            {
                var devices = libVlc.AudioOutputDevices(outputName);
                if (devices != null)
                {
                    foreach (var device in devices)
                    {
                        if (!string.IsNullOrWhiteSpace(device.DeviceIdentifier) && !string.IsNullOrWhiteSpace(device.Description))
                        {
                            list.Add(new UiOption(device.DeviceIdentifier, device.Description));
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // Safe fallback to the default device only. The failure is logged in full, with no dialog: the list
            // is a convenience and the default entry is always there (A12-2).
            HandlerBoundary.Report(nameof(GetAvailableAudioDevices), exception, notifyUser: false);
        }

        return list;
    }
}
