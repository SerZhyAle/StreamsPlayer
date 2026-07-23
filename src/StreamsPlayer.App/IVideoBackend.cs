using System.Windows;
using System.Windows.Media.Imaging;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026 video/RTSP playback seam. Isolates the concrete media engine (LibVLC by default,
/// FlyleafLib as an opt-in fallback) from <see cref="PlayerWindow"/>, which keeps the
/// engine-agnostic orchestration: recovery policy, stall watchdog, fullscreen, controls,
/// failure dialog, and thumbnail hand-off. Units are engine-neutral (milliseconds, cache %).
/// </summary>
internal interface IVideoBackend
{
    /// <summary>The WPF element hosting the video surface; inserted into the player's video host.</summary>
    FrameworkElement View { get; }

    /// <summary>
    /// Hosts the player's control overlay <em>inside</em> the native video surface so it floats above
    /// the video and survives window resizes. Both engines render the video on a native (airspace)
    /// surface that paints over sibling WPF elements; routing the overlay through the surface's own
    /// content is the only way to keep the panel visible on top.
    /// </summary>
    void SetOverlay(FrameworkElement overlay);

    /// <summary>Current playback position in milliseconds, or a negative value when unknown.</summary>
    long PositionMs { get; }

    /// <summary>True while the engine reports an active playing state (watchdog input).</summary>
    bool IsPlaying { get; }

    int Volume { set; }
    bool Mute { set; }

    /// <summary>
    /// Opens and plays a live URL. <paramref name="cacheMilliseconds"/> sizes the live buffer
    /// (initial vs reconnect is decided by the caller). Returns false if the engine rejects the play.
    /// </summary>
    bool Play(Uri url, uint cacheMilliseconds, bool rtspOverTcp, bool softwareDecode);

    /// <summary>Stops and disposes the engine off the UI thread; safe to call once during teardown.</summary>
    Task StopAndDisposeAsync();

    /// <summary>Requests a snapshot of the current frame; the result arrives via <see cref="SnapshotReady"/>.</summary>
    bool RequestSnapshot(int width);

    IReadOnlyList<VideoTrack> AudioTracks { get; }
    IReadOnlyList<VideoTrack> SubtitleTracks { get; }
    int SelectedAudioTrackId { get; }
    int SelectedSubtitleTrackId { get; }
    void SelectAudioTrack(int id);
    void SelectSubtitleTrack(int id);

    /// <summary>Logs engine-specific playback statistics under the given tag (no-op where unsupported).</summary>
    void LogStats(string tag);

    /// <summary>Buffer fill percentage 0..100.</summary>
    event Action<float> BufferingChanged;
    event Action EndReached;
    event Action EncounteredError;
    event Action TracksChanged;
    event Action<BitmapSource> SnapshotReady;
}

/// <summary>An engine-neutral audio or subtitle track descriptor for the player's track menus.</summary>
internal readonly record struct VideoTrack(int Id, string? Name);
