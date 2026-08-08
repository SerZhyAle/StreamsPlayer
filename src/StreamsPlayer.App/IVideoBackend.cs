using System.Windows;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

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

    /// <summary>
    /// Which engine this is, for the log. SP-0071 made it necessary: the quality ceiling is expressed
    /// through a different mechanism in each engine and is only verified in one of them, so a line
    /// reporting a ceiling has to say who received it. The factory falls back to LibVLC silently on a
    /// successful fallback path, so the selected setting is not the answer - the object is.
    /// </summary>
    string EngineName { get; }

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
    /// <param name="qualityCeiling">
    /// SP-0071: the highest rendition of an adaptive stream this open may select, or null for "whatever
    /// the engine would have chosen". Applied at open time because both engines fix adaptive selection
    /// there - which is also why a ceiling change costs a re-open rather than a setting.
    /// <para>Usually a rung of the stream's own ladder, so at least one rendition satisfies it. SP-0076
    /// added the one exception: on the first open the value may come from what an earlier session
    /// recorded, before any ladder has been read, and a source that has re-encoded upward since can leave
    /// every rendition above it. The player owns that risk - it drops the remembered ceiling on the first
    /// re-open of a session that never reached live - so an engine still just applies what it is given.</para>
    /// <para>An engine that cannot express the limit must leave its behaviour alone and let the player
    /// record that it did, rather than claim a cap that does not cap.</para>
    /// </param>
    bool Play(Uri url, uint cacheMilliseconds, bool rtspOverTcp, bool softwareDecode, StreamQualityRung? qualityCeiling);

    /// <summary>
    /// Stops and disposes the engine, doing the blocking native teardown off the UI thread; the view-side
    /// release that follows it is UI-thread work, so call this from the UI thread. Safe to call once during teardown.
    /// </summary>
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

    /// <summary>
    /// SP-0045: the decoder/demux loss counters for the signal-health stripe, as monotonic totals for
    /// the media currently open. Read at the player's existing stats cadence; it must not open, wait
    /// for, or poll anything of its own.
    /// <para>Null means this engine reports no counters, which the health rule reads as "no evidence
    /// of trouble" - never as trouble, so the less-instrumented engine cannot sit permanently yellow.</para>
    /// </summary>
    DecoderLossCounters? ReadLossCounters();

    /// <summary>
    /// SP-0070: how far the media currently open has got, as monotonic totals for the freeze rule to
    /// difference. Read on the watchdog's existing tick; like <see cref="ReadLossCounters"/> it must not
    /// open, wait for, or poll anything of its own.
    /// <para>Null means this engine reports no counters. The rule reads that as "no evidence", never as
    /// trouble - and never as a reason to stop watching: an engine without counters keeps the media-time
    /// watchdog it always had.</para>
    /// </summary>
    PlaybackProgressCounters? ReadProgressCounters();

    /// <summary>
    /// SP-0053: what this engine already knows about the stream it is playing, for the About window.
    /// Reads state that exists; never opens, re-opens, or waits for anything. Null where the engine
    /// does not describe its stream, or before it has one.
    /// </summary>
    StreamTransmission? DescribeTransmission();

    /// <summary>
    /// SP-0073: what the open stream says is on air right now - the track on a music station, the
    /// programme where the source announces one. Read on the player's existing stats tick; like
    /// <see cref="ReadLossCounters"/> it must not open, wait for, or poll anything of its own, and it
    /// must never open a second connection for metadata.
    /// <para>Null means the stream has said nothing yet. The rule reads that as "leave the line as it
    /// is", never as "the broadcast ended" - a re-opened media reports nothing until its first metadata
    /// block arrives, and erasing on that would blink the line through every reconnect.</para>
    /// <para>The value is untrusted broadcaster text. Bounding and sanitizing it is
    /// <see cref="NowPlayingTracker"/>'s job, not the backend's; an implementation returns what the
    /// engine gave it.</para>
    /// </summary>
    string? ReadNowPlaying();

    /// <summary>
    /// SP-0077: which rendition of an adaptive stream is on screen right now, as the engine already
    /// knows it. Read on the player's existing stats tick; like <see cref="ReadLossCounters"/> it must
    /// not open, wait for, or poll anything of its own.
    /// <para>This is the answer to the request <see cref="Play"/> carries. The ceiling is a limit, not a
    /// choice: inside it the engine picks a rendition itself and may change that pick mid-air, without a
    /// re-open and without telling the player - so a log holding only the ceiling cannot say what was
    /// actually shown, and cannot attribute a freeze to the rung it happened on.</para>
    /// <para>Null means the engine has nothing to report - it has no picture yet, or it does not
    /// describe one at all. The rule reads null as "unknown", never as "the ceiling was respected":
    /// silence from a less-instrumented engine must not be able to look like good news.</para>
    /// </summary>
    VideoRendition? ReadRendition();

    /// <summary>Buffer fill percentage 0..100.</summary>
    event Action<float> BufferingChanged;
    event Action EndReached;
    event Action EncounteredError;
    event Action TracksChanged;
    event Action<BitmapSource> SnapshotReady;
}

/// <summary>An engine-neutral audio or subtitle track descriptor for the player's track menus.</summary>
internal readonly record struct VideoTrack(int Id, string? Name);
