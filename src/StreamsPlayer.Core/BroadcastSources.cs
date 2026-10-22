namespace StreamsPlayer.Core;

/// <summary>The four sources of this PC a broadcast can carry (SP-0217; LIVE-BROADCAST amendment item A).</summary>
public enum BroadcastSource
{
    Microphone,
    SystemSound,
    Screen,
    Camera
}

/// <summary>
/// The source vocabulary of `LIVE-BROADCAST` item G and the per-source defaults SP-0217 fixes: the
/// `sourceId` form, the mode each source broadcasts, and the channel count decision the owner's answer
/// of 2026-10-10 adopted (the contract's own audio default for the microphone, stereo for the system
/// sound, both changeable in Settings).
/// </summary>
public static class BroadcastSources
{
    /// <summary>SP-0217 open question 1, the recommended answer: each source is its own broadcast; the bound is the owner's four sources.</summary>
    public const int MaximumSimultaneous = 4;

    public static string WireName(BroadcastSource source) => source switch
    {
        BroadcastSource.Microphone => "mic",
        BroadcastSource.SystemSound => "system-audio",
        BroadcastSource.Screen => "screen",
        BroadcastSource.Camera => "camera",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    public static bool TryParseWireName(string? wireName, out BroadcastSource source)
    {
        switch (wireName)
        {
            case "mic":
                source = BroadcastSource.Microphone;
                return true;
            case "system-audio":
                source = BroadcastSource.SystemSound;
                return true;
            case "screen":
                source = BroadcastSource.Screen;
                return true;
            case "camera":
                source = BroadcastSource.Camera;
                return true;
            default:
                source = BroadcastSource.Microphone;
                return false;
        }
    }

    /// <summary>
    /// Item G: a producer that offers more than one source writes `<deviceSourceId>:<source>`; a
    /// consumer treats the whole string as opaque. Stable across restarts because the device id is.
    /// </summary>
    public static string SourceId(string deviceId, BroadcastSource source) => $"{deviceId}:{WireName(source)}";

    public static string Mode(BroadcastSource source) => source switch
    {
        BroadcastSource.Microphone or BroadcastSource.SystemSound => FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
        BroadcastSource.Screen => FastMediaSorterBroadcastDescriptor.VideoOnlyMode,
        BroadcastSource.Camera => FastMediaSorterBroadcastDescriptor.VideoAudioMode,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    public static bool IsAudioOnly(BroadcastSource source) =>
        Mode(source) == FastMediaSorterBroadcastDescriptor.AudioOnlyMode;

    /// <summary>Open question 4, the recommended answer: mono for the microphone, stereo for the system sound.</summary>
    public static int DefaultChannels(BroadcastSource source) => source switch
    {
        BroadcastSource.Microphone => 1,
        BroadcastSource.SystemSound => 2,
        BroadcastSource.Screen => 1,
        BroadcastSource.Camera => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
}
