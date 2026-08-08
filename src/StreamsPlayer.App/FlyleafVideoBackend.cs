using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Flyleaf.FFmpeg;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;
using StreamsPlayer.Core;
using static Flyleaf.FFmpeg.Raw;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026 FlyleafLib (FFmpeg/DirectX) implementation of <see cref="IVideoBackend"/> - the opt-in,
/// experimental alternative engine. Maps the seam onto FlyleafLib 3.10.x. LibVLC remains the default.
/// </summary>
/// <remarks>
/// Experimental caveats (surfaced by the Settings "experimental" label, never as a crash):
/// the FFmpeg v8 native binaries are NOT delivered by NuGet - they must be deployed (x64 only) into
/// the folder resolved by <see cref="ResolveFFmpegPath"/>. If they are absent (or on win-arm64), the
/// constructor throws during engine start and <see cref="VideoBackendFactory"/> falls back to LibVLC.
/// FlyleafLib has no LibVLC-style live-statistics surface, so <see cref="LogStats"/> reports that gap
/// once per session (SP-0040) instead of reporting numbers it cannot obtain.
/// </remarks>
internal sealed class FlyleafVideoBackend : IVideoBackend
{
    private static bool _engineStarted;
    private static readonly object EngineLock = new();

    /// <summary>
    /// SP-0078: how often the live-edge distance is recorded when nothing about it changed. A session
    /// that never needed a correction must still prove the distance stayed in the corridor, or it cannot
    /// be told apart from a session where the rule never ran.
    /// </summary>
    private static readonly TimeSpan LiveEdgeHeartbeat = TimeSpan.FromMinutes(1);

    private readonly Player _player;
    private readonly Config _config;
    private readonly FlyleafHost _host;
    private readonly CurrentLog _log;
    private string _lastUrl = string.Empty;
    private bool _reachedPlaying;
    private bool _statsGapReported;
    private readonly Stopwatch _bufferClock = new();
    private readonly Stopwatch _liveEdgeClock = new();
    private double _liveEdgeSpeed = 1d;
    private int _selectedAudioPos = -1;
    private int _selectedSubtitlePos = -1;

    public FlyleafVideoBackend(int volume, bool muted, CurrentLog log)
    {
        _log = log;
        EnsureEngineStarted(log); // throws if the FFmpeg natives are unavailable -> factory falls back to LibVLC
        _config = new Config();
        _config.Player.AutoPlay = true;
        // rtsp-over-tcp and HTTP auto-reconnect are FlyleafLib defaults; per-play tuning happens in Play.
        _player = new Player(_config);
        try
        {
            _player.Audio.Volume = Math.Clamp(volume, 0, 100);
            _player.Audio.Mute = muted;
            _player.BufferingStarted += OnBufferingStarted;
            _player.BufferingCompleted += OnBufferingCompleted;
            _player.OpenCompleted += OnOpenCompleted;
            _player.PlaybackStopped += OnPlaybackStopped;
            _player.PropertyChanged += OnPlayerPropertyChanged;
            _host = new FlyleafHost { Player = _player };
        }
        catch
        {
            // The factory falls back to LibVLC on any construction failure; without this the half-built
            // player (and its FFmpeg/DirectX resources) would be unreachable and never disposed.
            _player.Dispose();
            throw;
        }
    }

    public FrameworkElement View => _host;

    // FlyleafHost is a ContentControl whose Content is presented on the overlay window above the
    // DirectX video surface; assigning it keeps the control panel on top of the video through resizes.
    public void SetOverlay(FrameworkElement overlay) => _host.Content = overlay;

    public string EngineName => "flyleaf";

    public long PositionMs => _player.CurTime / TimeSpan.TicksPerMillisecond;

    public bool IsPlaying => _player.IsPlaying;

    public int Volume { set => _player.Audio.Volume = Math.Clamp(value, 0, 100); }

    public bool Mute { set => _player.Audio.Mute = value; }

    public event Action<float>? BufferingChanged;
    public event Action? EndReached;
    public event Action? EncounteredError;
    public event Action? TracksChanged;
    public event Action<BitmapSource>? SnapshotReady;

    public bool Play(Uri url, uint cacheMilliseconds, bool rtspOverTcp, bool softwareDecode, StreamQualityRung? qualityCeiling)
    {
        _reachedPlaying = false;
        _selectedAudioPos = -1;
        _selectedSubtitlePos = -1;
        // SP-0078: every leg decides for itself whether it has a live edge, so the previous leg's answer
        // is cleared here rather than at teardown. Clearing it returns the speed to normal and restores
        // the decoder's own delay handling, which is free to do while nothing is playing and wrong to
        // leave standing for a source that turns out not to be live.
        _config.Player.MaxLatency = 0;
        _liveEdgeSpeed = 1d;
        _liveEdgeClock.Reset();
        _config.Video.VideoAcceleration = !softwareDecode; // force software decode when requested
        _config.Demuxer.BufferDuration = (long)cacheMilliseconds * TimeSpan.TicksPerMillisecond;
        // SP-0071: FlyleafLib picks the video stream when the media is opened, and FFmpeg exposes each
        // HLS rendition as a stream of its own - so this is the same open-time ceiling LibVLC gets from
        // :adaptive-maxheight, under the library's own name. 0 is its "no custom limit".
        _config.Video.MaxVerticalResolutionCustom = qualityCeiling?.Height ?? 0;
        if (rtspOverTcp)
        {
            _config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        }

        _lastUrl = url.ToString();
        _player.OpenAsync(_lastUrl); // rejection/failure surfaces via OpenCompleted / PlaybackStopped -> EncounteredError
        return true;
    }

    public Task StopAndDisposeAsync()
    {
        _player.BufferingStarted -= OnBufferingStarted;
        _player.BufferingCompleted -= OnBufferingCompleted;
        _player.OpenCompleted -= OnOpenCompleted;
        _player.PlaybackStopped -= OnPlaybackStopped;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _host.Player = null;
        // FlyleafLib disposal is internally marshaled; the caller (PlayerWindow_Closed) is on the UI thread.
        _player.Dispose();
        // Player = null only disposes the swap chain. FlyleafHost.Dispose is what closes the Surface and
        // Overlay windows it owns and unhooks the DpiChanged/SizeChanged handlers it put on the parent window.
        _host.Dispose();
        return Task.CompletedTask;
    }

    public bool RequestSnapshot(int width)
    {
        try
        {
            var frame = _player.TakeSnapshotToBitmapSource((uint)width);
            _log.Event("THUMB SNAPSHOT", $"ok={frame is not null}", $"url={_lastUrl}");
            if (frame is null)
            {
                return false;
            }

            SnapshotReady?.Invoke(frame);
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort thumbnail only (no frame available / renderer not ready); never crash the UI over it.
            _log.Event("THUMB SNAPSHOT", "ok=exception", $"err={ex.Message}", $"url={_lastUrl}");
            return false;
        }
    }

    public IReadOnlyList<VideoTrack> AudioTracks => Map(_player.Audio.Streams);

    public IReadOnlyList<VideoTrack> SubtitleTracks => Map(_player.Subtitles.Streams);

    public int SelectedAudioTrackId => _selectedAudioPos;

    public int SelectedSubtitleTrackId => _selectedSubtitlePos;

    public void SelectAudioTrack(int id)
    {
        if (id >= 0 && id < _player.Audio.Streams.Count)
        {
            _selectedAudioPos = id;
            _player.OpenAsync(_player.Audio.Streams[id]);
        }
    }

    public void SelectSubtitleTrack(int id)
    {
        if (id >= 0 && id < _player.Subtitles.Streams.Count)
        {
            _selectedSubtitlePos = id;
            _player.OpenAsync(_player.Subtitles.Streams[id]);
        }
    }

    // FlyleafLib exposes no LibVLC-style input/demux counter surface; documented experimental gap.
    // SP-0040: say so once per session instead of staying silent - in an archived log, "this engine
    // reports no statistics" and "this session had no trouble" are otherwise indistinguishable.
    public void LogStats(string tag)
    {
        LogLiveEdge(); // SP-0078 rides this existing tick rather than adding one of its own
        if (_statsGapReported)
        {
            return;
        }

        _statsGapReported = true;
        _log.Event("FLYLEAF STATS", "stats=unavailable", $"url={_lastUrl}");
    }

    // SP-0045: the same documented gap as LogStats - there are no LibVLC-style loss counters to read.
    // Null, not zeroes: zeroes would be a claim that nothing is being lost, which this engine cannot make.
    // The health rule treats null as no evidence of trouble, so this backend's stripe stays green rather
    // than stuck yellow (SP-0045 acceptance 8).
    public DecoderLossCounters? ReadLossCounters() => null;

    // SP-0070: the same documented gap again - this engine exposes no displayed-picture or demux-byte
    // totals. Null, not zeroes: zeroes would be a claim that nothing is arriving, which is exactly the
    // freeze this backend would then report on every healthy stream. With null the freeze rule keeps
    // judging this engine by media time, which is the watchdog it had before SP-0070.
    public PlaybackProgressCounters? ReadProgressCounters() => null;

    // SP-0053: the same documented gap as LogStats. This backend's stream description is not exposed in
    // the shape the About window needs, and a partly-filled reading would be indistinguishable from a
    // stream that carries no audio - so this engine reports nothing rather than something wrong.
    public StreamTransmission? DescribeTransmission() => null;

    // SP-0077: unlike the three readings above, this one this engine can answer. Player.Video carries the
    // dimensions of the video stream currently open, checked against FlyleafLib 3.10.4 by reflection
    // rather than assumed - the package XML documents neither Width nor Height.
    //
    // Expect one answer per leg here and no mid-air switch: FFmpeg exposes each HLS rendition as a
    // separate stream and FlyleafLib picks one when the media is opened, which is the same reason its
    // ceiling (MaxVerticalResolutionCustom) is an open-time setting. A constant reading on this engine is
    // therefore a fact about it, not a gap in it.
    public VideoRendition? ReadRendition() =>
        _player.Video is { IsOpened: true, Width: > 0, Height: > 0 } video
            ? new VideoRendition(video.Width, video.Height)
            : null;

    /// <summary>
    /// SP-0073: what the stream says is on air, read from the demuxer's own format context.
    /// </summary>
    /// <remarks>
    /// The one place in this assembly that needs pointer syntax, and the reason is measured rather than
    /// assumed: FlyleafLib fills <c>Demuxer.Metadata</c> once, in <c>FillInfo()</c> during <c>Open()</c>,
    /// and nothing in its demux loop refreshes it - the library never checks FFmpeg's
    /// <c>AVFMT_EVENT_FLAG_METADATA_UPDATED</c>. Polling that property would therefore report whatever
    /// the stream said at open, unchanged, for the life of the leg. The owner asked for parity with the
    /// LibVLC engine knowing this cost.
    /// <para>What is read instead is FFmpeg's live <c>icy_metadata_packet</c> option on the AVIOContext,
    /// which the HTTP protocol rewrites as each metadata block arrives. Its payload is the
    /// <c>StreamTitle='..';</c> block <see cref="IcyMetadataParser"/> already parses, so nothing new is
    /// needed to read it. <c>fmtCtx-&gt;metadata</c> is the fallback for a source that routes the title
    /// through the format dictionary rather than through ICY.</para>
    /// <para>Two rules make this safe to run from the UI thread's stats tick while the window may be
    /// closing: everything touching the context happens under FlyleafLib's own <c>lockFmtCtx</c>, which
    /// is the lock the library takes when it disposes that context, and every failure path returns null
    /// rather than throwing at a caller that is repainting a panel.</para>
    /// </remarks>
    public unsafe string? ReadNowPlaying()
    {
        var demuxer = _player.MainDemuxer;
        if (demuxer is null)
        {
            return null;
        }

        lock (demuxer.lockFmtCtx)
        {
            var context = demuxer.FormatContext;
            if (context is null)
            {
                return null;
            }

            var block = ReadOption(context->pb, "icy_metadata_packet");
            var title = block is null ? null : IcyMetadataParser.ExtractStreamTitle(block);
            return title ?? ReadDictionary(context->metadata, "StreamTitle");
        }
    }

    /// <summary>Reads a string AVOption, or null when it is absent, empty or unreadable.</summary>
    /// <remarks>
    /// <c>av_opt_get</c> allocates the buffer it returns and the caller owns it, so the
    /// <c>av_free</c> is not optional: this runs every two seconds for the whole session.
    /// </remarks>
    private static unsafe string? ReadOption(void* target, string name)
    {
        if (target is null)
        {
            return null;
        }

        byte* value = null;
        if (av_opt_get(target, name, OptSearchFlags.Children, &value) < 0 || value is null)
        {
            return null;
        }

        try
        {
            var text = Marshal.PtrToStringUTF8((nint)value);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            av_free(value);
        }
    }

    /// <summary>Reads one key of an FFmpeg dictionary, or null when it is absent or empty.</summary>
    private static unsafe string? ReadDictionary(AVDictionary* dictionary, string key)
    {
        if (dictionary is null)
        {
            return null;
        }

        var entry = av_dict_get(dictionary, key, null, DictReadFlags.None);
        if (entry is null)
        {
            return null;
        }

        var text = Marshal.PtrToStringUTF8((nint)entry->value);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static VideoTrack[] Map<T>(IEnumerable<T> streams) =>
        streams.Select((stream, index) => new VideoTrack(index, stream?.ToString())).ToArray();

    private void OnBufferingStarted(object? sender, EventArgs e)
    {
        _bufferClock.Restart();
        _log.Event("FLYLEAF BUFFER", "state=started", $"url={_lastUrl}");
        BufferingChanged?.Invoke(0f);
    }

    private void OnBufferingCompleted(object? sender, BufferingCompletedArgs e)
    {
        // SP-0040: on this backend a stall has no statistics behind it, so the fill time and the success
        // flag are the only evidence of how badly the stream is behaving. Record them.
        _log.Event("FLYLEAF BUFFER", "state=completed", $"ok={e.Success}", $"fill_ms={_bufferClock.ElapsedMilliseconds}", $"url={_lastUrl}");
        if (e.Success)
        {
            BufferingChanged?.Invoke(100f);
        }
        else
        {
            _log.Event("FLYLEAF ERROR", "source=buffering", $"url={_lastUrl}");
            EncounteredError?.Invoke();
        }
    }

    private void OnOpenCompleted(object? sender, OpenCompletedArgs e)
    {
        if (e.Success)
        {
            TracksChanged?.Invoke(); // embedded streams are populated after a successful open
            ApplyLiveEdgeCorridor(); // SP-0078: only now is it known whether this source has a live edge
        }
        else
        {
            _log.Event("FLYLEAF ERROR", "source=open", $"url={_lastUrl}");
            EncounteredError?.Invoke();
        }
    }

    /// <summary>
    /// SP-0078: hands the engine the distance it should hold from the live edge, on a live source only.
    /// </summary>
    /// <remarks>
    /// The engine's own latency controller is the entire implementation, and deliberately so. Its public
    /// speed property re-buffers on every accepted assignment, so a rate loop written here would produce
    /// exactly the stutter this rule exists to prevent; setting the two durations is the only route into
    /// the path that changes speed without re-buffering. What that path then does is not ours to shape:
    /// it never nudges by less than 1.1x, which is why the corridor's job is to make the nudge rare and
    /// short rather than small.
    /// <para>The demuxer is read directly rather than through <c>Player.IsLive</c>, whose getter
    /// dereferences a demuxer that is null before the first open and after teardown.</para>
    /// <para>Setting the target also raises the engine's read-ahead buffer past the one <see cref="Play"/>
    /// asked for, so the line reports the buffer the engine ended up with next to the one the corridor
    /// claims. A disagreement between them is then evidence rather than silence.</para>
    /// </remarks>
    private void ApplyLiveEdgeCorridor()
    {
        if (_player.MainDemuxer?.IsLive != true)
        {
            // Acceptance 3, and the general rule behind it: "the rule did not apply" and "the rule never
            // ran" must not look the same in an archived log.
            _log.Event("FLYLEAF LIVE EDGE", "applied=no", "reason=not_live", $"url={_lastUrl}");
            return;
        }

        var corridor = LiveEdgeCorridor.Default;
        _config.Player.MinLatency = corridor.Floor.Ticks;
        _config.Player.MaxLatency = corridor.Target.Ticks;
        _liveEdgeClock.Restart();
        _log.Event(
            "FLYLEAF LIVE EDGE",
            "applied=yes",
            corridor.Describe(),
            $"engine_buffer_ms={_config.Demuxer.BufferDuration / TimeSpan.TicksPerMillisecond}",
            $"url={_lastUrl}");
    }

    /// <summary>
    /// SP-0078: what the live-edge rule actually did, recorded on the tick the player already runs.
    /// </summary>
    /// <remarks>
    /// A line whenever the engine changed speed, and otherwise one a minute. Both are needed: the speed
    /// changes are the correction and its cost, while the heartbeat is what stops a session that needed
    /// no correction from reading like a session where the rule never ran. Silent while the rule is off,
    /// so a non-live source and a leg that never opened add nothing.
    /// <para>The distance is the demuxer's packet span. The controller compares against that plus the
    /// decoded frames still queued - a few frames more - so this reading runs marginally short of the
    /// number it acted on, and is the public one that will outlive a library update. A negative value
    /// means there was no demuxer to ask, never that the picture is at the edge.</para>
    /// </remarks>
    private void LogLiveEdge()
    {
        if (_config.Player.MaxLatency == 0)
        {
            return;
        }

        var speed = _player.Speed;
        var changed = Math.Abs(speed - _liveEdgeSpeed) > 0.001;
        if (!changed && _liveEdgeClock.Elapsed < LiveEdgeHeartbeat)
        {
            return;
        }

        _liveEdgeSpeed = speed;
        _liveEdgeClock.Restart();
        var distanceTicks = _player.MainDemuxer?.BufferedDuration;
        _log.Event(
            "FLYLEAF LIVE EDGE",
            string.Create(CultureInfo.InvariantCulture, $"speed={speed:0.0#}"),
            $"distance_ms={(distanceTicks / TimeSpan.TicksPerMillisecond) ?? -1}",
            changed ? "cause=speed_changed" : "cause=heartbeat",
            $"url={_lastUrl}");
    }

    private void OnPlaybackStopped(object? sender, PlaybackStoppedArgs e)
    {
        if (e.Success)
        {
            EndReached?.Invoke(); // reached end of a live stream -> bounded recovery re-anchors to the live edge
        }
        else
        {
            _log.Event("FLYLEAF ERROR", "source=playback_stopped", $"url={_lastUrl}");
            EncounteredError?.Invoke();
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // First transition into Playing is the "reached live" signal (drives outcome + thumbnail in the window).
        if (e.PropertyName == nameof(Player.Status) && _player.Status == Status.Playing && !_reachedPlaying)
        {
            _reachedPlaying = true;
            BufferingChanged?.Invoke(100f);
        }
    }

    private static void EnsureEngineStarted(CurrentLog log)
    {
        if (_engineStarted)
        {
            return;
        }

        lock (EngineLock)
        {
            if (_engineStarted)
            {
                return;
            }

            var ffmpegPath = ResolveFFmpegPath(out var source);
            log.Event("FLYLEAF ENGINE", "action=start", $"ffmpeg_path={ffmpegPath}", $"source={source}");
            Engine.Start(new EngineConfig
            {
                FFmpegPath = ffmpegPath,
                UIRefresh = false
            });
            _engineStarted = true;
        }
    }

    /// <summary>
    /// Where the FFmpeg natives are looked for. A complete set beside the executable wins, which keeps
    /// a hand-deployed folder working and lets it override the managed one; otherwise the per-user
    /// folder the Settings installer writes to. Missing natives make <c>Engine.Start</c> throw, and
    /// <see cref="VideoBackendFactory"/> falls back to LibVLC.
    /// </summary>
    /// <remarks>
    /// The per-user folder is the one that works on every channel: an MSIX install lives under the
    /// protected <c>WindowsApps</c> tree, where nothing can be placed beside the executable at all.
    /// </remarks>
    internal static string ResolveFFmpegPath(out string source)
    {
        var besideExecutable = FFmpegComponents.ResolveFolder(AppContext.BaseDirectory);
        if (FFmpegComponents.IsInstalled(besideExecutable))
        {
            source = "app";
            return besideExecutable;
        }

        source = "user";
        return FFmpegComponents.ResolveFolder(AppPaths.DataDirectory);
    }
}
