using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026 LibVLC implementation of <see cref="IVideoBackend"/> - the proven default engine.
/// This is a behaviour-preserving extraction of the LibVLC surface that previously lived directly
/// in <see cref="PlayerWindow"/>: the exact option set, per-media caching, snapshot pipeline, track
/// enumeration, live statistics, and the Play/teardown race protection (<see cref="_mediaGate"/>).
/// </summary>
internal sealed class LibVlcVideoBackend : IVideoBackend
{
    // SP-0054: the width of the window inside which LibVLC may compensate input-clock jitter.
    // `--clock-jitter` is a compensation *budget*, not a leniency switch: VLC's own help calls it "the
    // maximal input jitter that is considered valid and can be compensated", and jitter beyond it is
    // left uncompensated - the clock reference is dropped, the audio output fills with silence and the
    // video output discards pictures as "too early". The shipped 0 therefore compensated nothing, which
    // is the opposite of what its comment claimed. In the 2026-08-07 tester log that cost 156 lost clock
    // references, 63 silence fills and 46 early-skipped pictures, and only 20-50 % of decoded frames
    // reached the screen while `lost_pics` stayed at 0. 1000 ms covers every steady-state jitter sample
    // in that log (100-335 ms) while still capping the latency growth VLC's 5000 ms default permits.
    private const int ClockJitterMilliseconds = 1000;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly LibVLCSharp.WPF.VideoView _videoView;
    private readonly CurrentLog _log;
    // Serializes native Play against Stop/Dispose so a background reconnect can never race teardown.
    private readonly object _mediaGate = new();
    private Media? _media;
    private string _lastUrl = string.Empty;
    // Baselines for the per-second rates published by LogStats; reset per open so a reconnect does not
    // report the previous leg's counters as a spike. Written on the UI thread under _mediaGate (Play) and
    // on the stats tick (LogStats), which is the same UI thread - no cross-thread sharing.
    private long _rateBytes;
    private long _rateDisplayed;
    private long _rateTicks;
    private double _rateSeconds;
    // Written under _mediaGate on the teardown thread, read without it by the audio setters below.
    private volatile bool _disposed;

    public LibVlcVideoBackend(int volume, bool muted, CurrentLog log)
    {
        _log = log;
        LibVLCSharp.Shared.Core.Initialize();
        // avcodec-hw=none is set here, instance-wide, so it pins software decode for every stream this
        // backend ever plays and overrides the per-stream softwareDecode argument of Play. That is
        // deliberate for now - it avoids the GPU surface starvation that caused the original freezes -
        // but it means only FlyleafVideoBackend actually honours that argument. Making the LibVLC path
        // honour it too is a decode-path change and must be measured on its own, not folded into a
        // clock fix (SP-0054).
        // Late frames are still dropped, and that is correct here: the pictures lost in SP-0054's
        // evidence were discarded as *early*, not late, so --no-drop-late-frames cannot address them.
        // The freeze watchdog reconnects if the pipeline fully deadlocks. (--no-ts-trust-pcr was tried
        // and reverted: it removes the clock reference entirely and deadlocks the vout at 0 fps.)
        _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--rtsp-tcp", $"--clock-jitter={ClockJitterMilliseconds}", "--avcodec-hw=none");
        _libVlc.Log += LibVlc_Log;
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Volume = Math.Clamp(volume, 0, 100);
        _mediaPlayer.Mute = muted;
        _mediaPlayer.Buffering += MediaPlayer_Buffering;
        _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
        _mediaPlayer.Opening += MediaPlayer_Opening;
        _mediaPlayer.Playing += MediaPlayer_Playing;
        _mediaPlayer.Paused += MediaPlayer_Paused;
        _mediaPlayer.Stopped += MediaPlayer_Stopped;
        _mediaPlayer.EndReached += MediaPlayer_EndReached;
        _mediaPlayer.ESAdded += MediaPlayer_TracksChanged;
        _mediaPlayer.ESSelected += MediaPlayer_TracksChanged;
        _mediaPlayer.SnapshotTaken += MediaPlayer_SnapshotTaken;
        _videoView = new LibVLCSharp.WPF.VideoView { MediaPlayer = _mediaPlayer };
    }

    public FrameworkElement View => _videoView;

    // VideoView is a ContentControl backed by a foreground overlay window painted above the native
    // VLC surface; its Content is the only WPF layer that stays on top of the video through resizes.
    public void SetOverlay(FrameworkElement overlay) => _videoView.Content = overlay;

    public string EngineName => "libvlc";

    public long PositionMs => _mediaPlayer.Time;

    public bool IsPlaying => _mediaPlayer.State == VLCState.Playing;

    public int Volume { set => ApplyAudio(player => player.Volume = Math.Clamp(value, 0, 100), "volume"); }

    public bool Mute { set => ApplyAudio(player => player.Mute = value, "mute"); }

    // Both setters are driven from the UI thread while teardown may already be running on a worker
    // thread, and a native call on a stopped or disposed MediaPlayer ends the process without a managed
    // exception for the log to carry. An unwritten audio setting is worth far less than the session.
    private void ApplyAudio(Action<MediaPlayer> change, string what)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            change(_mediaPlayer);
        }
        catch (VLCException ex)
        {
            _log.Event("AUDIO SET", "ok=false", $"what={what}", $"err={ex.Message}", $"url={_lastUrl}");
        }
        catch (ObjectDisposedException)
        {
            // Raced the teardown that runs off the UI thread; the player is gone and so is the setting.
        }
    }

    public event Action<float>? BufferingChanged;
    public event Action? EndReached;
    public event Action? EncounteredError;
    public event Action? TracksChanged;
    public event Action<BitmapSource>? SnapshotReady;

    public bool Play(Uri url, uint cacheMilliseconds, bool rtspOverTcp, bool softwareDecode, StreamQualityRung? qualityCeiling)
    {
        lock (_mediaGate)
        {
            if (_disposed)
            {
                return false; // backend is tearing down; do not touch the (soon) disposed player
            }

            _lastUrl = url.ToString();
            _rateBytes = 0;
            _rateDisplayed = 0;
            _rateTicks = 0;
            _rateSeconds = 0;
            _mediaPlayer.NetworkCaching = cacheMilliseconds;
            // SP-0069: the field takes ownership only once Play has returned. Assigning it first and
            // disposing the previous wrapper afterwards - which is what this did - strands one Media per
            // throwing Play, and on the recovery leg that throw lands inside a Task.Run nobody observes.
            // The previous wrapper is still disposed after Play rather than before it, because the player
            // may be reading from it until the new one takes effect.
            var previous = _media;
            var next = new Media(_libVlc, url);
            try
            {
                next.AddOption($":network-caching={cacheMilliseconds}");
                next.AddOption($":live-caching={cacheMilliseconds}");
                if (rtspOverTcp)
                {
                    next.AddOption(":rtsp-tcp");
                }

                if (softwareDecode)
                {
                    next.AddOption(":avcodec-hw=none");
                }

                // SP-0071: libvlc's adaptive demuxer excludes a rendition whose width *or* height exceeds
                // the limit, so both are set - a source may declare only one of them usefully. Verified
                // against the shipped plugin rather than assumed: adaptive-maxwidth ("Maximum device
                // width") and adaptive-maxheight ("Maximum device height") are both present in
                // VideoLAN.LibVLC.Windows 3.0.23.1's libadaptive_plugin.dll.
                // The representation selector has nothing to fall back to when *every* rendition exceeds
                // the limit, so a ceiling must never be invented from something the stream did not offer -
                // a screen size, for instance. SP-0076's remembered ceiling was a rung of this stream's
                // own ladder when it was recorded, which can go stale; the player, not this method, is
                // where that risk is bounded.
                if (qualityCeiling is { } ceiling)
                {
                    next.AddOption($":adaptive-maxwidth={ceiling.Width}");
                    next.AddOption($":adaptive-maxheight={ceiling.Height}");
                }

                var started = _mediaPlayer.Play(next);
                _media = next;
                previous?.Dispose();
                return started;
            }
            finally
            {
                // Reached with the field still naming `previous` only when Play threw past the assignment.
                if (!ReferenceEquals(_media, next))
                {
                    next.Dispose();
                }
            }
        }
    }

    public async Task StopAndDisposeAsync()
    {
        _videoView.MediaPlayer = null; // detach from the WPF VideoView on the UI thread (fast, non-blocking)
        _mediaPlayer.Buffering -= MediaPlayer_Buffering;
        _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
        _mediaPlayer.Opening -= MediaPlayer_Opening;
        _mediaPlayer.Playing -= MediaPlayer_Playing;
        _mediaPlayer.Paused -= MediaPlayer_Paused;
        _mediaPlayer.Stopped -= MediaPlayer_Stopped;
        _mediaPlayer.EndReached -= MediaPlayer_EndReached;
        _mediaPlayer.ESAdded -= MediaPlayer_TracksChanged;
        _mediaPlayer.ESSelected -= MediaPlayer_TracksChanged;
        _mediaPlayer.SnapshotTaken -= MediaPlayer_SnapshotTaken;
        _libVlc.Log -= LibVlc_Log;

        // Stop()/Dispose() block until the native VLC worker threads settle; on a flapping stream that
        // can take seconds and would freeze the shared WPF UI thread. Tear down off the UI thread.
        // _mediaGate serializes this against any in-flight reconnect Play so they never race natively.
        var mediaPlayer = _mediaPlayer;
        var libVlc = _libVlc;
        // SP-0069: staged, because these five releases used to share one unprotected block. The comment
        // above says Stop() can take seconds on a flapping stream - the very case where it is most
        // likely to fault - and a throw there skipped the Media, the MediaPlayer, the LibVLC instance
        // and the VideoView below, leaking a whole native engine with its worker threads and its child
        // HWND. The caller cannot await this task, so the fault was invisible until some later GC.
        await Task.Run(() =>
        {
            lock (_mediaGate)
            {
                _disposed = true;
                ReleaseStage("stop", mediaPlayer.Stop);
                ReleaseStage("media", () =>
                {
                    _media?.Dispose();
                    _media = null;
                });
                ReleaseStage("player", mediaPlayer.Dispose);
                ReleaseStage("engine", libVlc.Dispose);
            }
        });

        // Back on the UI thread (the caller is the UI thread and this await is not configured away).
        // Detaching the player does not release what the view owns: VideoView.Dispose is the only path in
        // LibVLCSharp that destroys the per-view child HWND (VideoHwndHost) and closes the ForegroundWindow
        // overlay. Runs after the native stop so no vout is still rendering into that HWND; Dispose is
        // idempotent and Window.Close on an already-closed overlay is a no-op.
        ReleaseStage("view", _videoView.Dispose);
    }

    /// <summary>Run one native release so a fault in it cannot skip the releases that follow.</summary>
    /// <remarks>
    /// SP-0069: the filter is narrow on purpose - a native stop or dispose reports through
    /// <see cref="VLCException"/>, and a doubly-torn-down wrapper through
    /// <see cref="ObjectDisposedException"/>. Anything else is not a teardown failure and must keep
    /// propagating. A failed stage is logged rather than swallowed: the whole point of the staging is
    /// that the next stage still runs, so the record of what did not close is the only trace left.
    /// </remarks>
    private void ReleaseStage(string stage, Action release)
    {
        try
        {
            release();
        }
        catch (Exception ex) when (ex is VLCException or ObjectDisposedException)
        {
            _log.Event("PLAYBACK TEARDOWN", $"stage={stage}", "ok=false", $"err={ex.Message}", $"url={_lastUrl}");
        }
    }

    public bool RequestSnapshot(int width)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"streamsplayer_thumb_{Guid.NewGuid():N}.png");
            var ok = _mediaPlayer.TakeSnapshot(0, path, (uint)width, 0); // aspect preserved; result arrives via SnapshotTaken
            _log.Event("THUMB SNAPSHOT", $"ok={ok}", $"url={_lastUrl}");
            return ok;
        }
        catch (VLCException ex)
        {
            _log.Event("THUMB SNAPSHOT", $"ok=exception", $"err={ex.Message}", $"url={_lastUrl}");
            return false;
        }
    }

    public IReadOnlyList<VideoTrack> AudioTracks => Describe(_mediaPlayer.AudioTrackDescription);

    public IReadOnlyList<VideoTrack> SubtitleTracks => Describe(_mediaPlayer.SpuDescription);

    public int SelectedAudioTrackId => _mediaPlayer.AudioTrack;

    public int SelectedSubtitleTrackId => _mediaPlayer.Spu;

    public void SelectAudioTrack(int id) => _mediaPlayer.SetAudioTrack(id);

    public void SelectSubtitleTrack(int id) => _mediaPlayer.SetSpu(id);

    // The two derived rates carry the diagnosis: in_kbps separates real network starvation from a stream
    // that is arriving fine, and disp_fps says whether the screen is actually getting frames. The totals
    // beside them are kept because they still mean something outside HLS, and because lost_pics/corrupted/
    // discont are what SP-0045's health stripe differences - but see Rate for why they mislead on their own.
    public void LogStats(string tag)
    {
        // The Media getter retains the native media on every call and LibVLCSharp has no finalizer, so the
        // wrapper must be disposed here: the stats timer ticks every 2 s and an undisposed wrapper would
        // leave each played media unfreeable for the life of the process.
        using var media = _mediaPlayer.Media;
        if (media is null)
        {
            return;
        }

        var s = media.Statistics;
        OpenRateInterval();
        _log.Event(tag,
            $"read_bytes={s.ReadBytes}",
            $"in_bitrate={s.InputBitrate:F4}",
            $"demux_bytes={s.DemuxReadBytes}",
            $"demux_bitrate={s.DemuxBitrate:F4}",
            $"in_kbps={Rate(ref _rateBytes, (long)s.DemuxReadBytes, 8d / 1000d)}",
            $"decoded_v={s.DecodedVideo}",
            $"displayed={s.DisplayedPictures}",
            $"disp_fps={Rate(ref _rateDisplayed, (long)s.DisplayedPictures, 1d)}",
            $"lost_pics={s.LostPictures}",
            $"corrupted={s.DemuxCorrupted}",
            $"discont={s.DemuxDiscontinuity}",
            $"url={_lastUrl}");
    }

    /// <summary>
    /// Differences a monotonic counter against the previous sample and scales it to a per-second rate.
    /// <para>Two counters are published this way, and both exist because the totals beside them mislead.
    /// <c>in_kbps</c>: libvlc fills <c>ReadBytes</c>/<c>InputBitrate</c> from the access module alone,
    /// but the HLS and DASH demuxers fetch segments through their own downloader below that layer, so on
    /// every <c>.m3u8</c> both stay frozen - a 2026-08-07 log held <c>read_bytes=364</c> and
    /// <c>in_bitrate=0</c> across a whole 124 s session, and a local run measured 1.9 Mbps arriving while
    /// <c>in_bitrate</c> still read 0. <c>disp_fps</c>: the rate frames actually reach the screen, which
    /// is the one number a "the stream is jerky" report can be checked against. Do not try to derive that
    /// from <c>decoded_v - displayed</c>: <c>decoded_v</c> ran at almost exactly twice <c>displayed</c>
    /// on a stream measured to be playing smoothly at its full frame rate, so the difference between
    /// those two counters is a counting-semantics artifact, not loss.</para>
    /// <para><paramref name="previous"/> carries the last sample and is updated in place; the shared
    /// timestamp is <see cref="_rateTicks"/>, so every counter published in one <see cref="LogStats"/>
    /// call is differenced over the same interval.</para>
    /// </summary>
    private string Rate(ref long previous, long current, double scale)
    {
        var previousValue = previous;
        previous = current;
        if (_rateSeconds <= 0 || current < previousValue)
        {
            // No prior sample for this leg, or the counter went backwards because the demux restarted
            // underneath us. Report no rate rather than a fabricated or negative one.
            return "n/a";
        }

        return ((current - previousValue) * scale / _rateSeconds).ToString("F1");
    }

    /// <summary>
    /// Advances the shared rate interval to now and returns the seconds elapsed since the previous
    /// <see cref="LogStats"/> call, or 0 for the first call of a leg. Called once per sample, before any
    /// <see cref="Rate"/> call, so the counters cannot disagree about the interval they span.
    /// </summary>
    private void OpenRateInterval()
    {
        var ticks = Stopwatch.GetTimestamp();
        _rateSeconds = _rateTicks == 0 ? 0 : (ticks - _rateTicks) / (double)Stopwatch.Frequency;
        _rateTicks = ticks;
    }

    // SP-0045: the same three counters LogStats prints, as numbers the health rule can difference.
    // Same Media-wrapper discipline as LogStats - the getter retains the native media on every call,
    // and this runs on the same 2 s tick, so an undisposed wrapper would leak a media per sample.
    public DecoderLossCounters? ReadLossCounters()
    {
        if (_disposed)
        {
            return null;
        }

        using var media = _mediaPlayer.Media;
        if (media is null)
        {
            return null;
        }

        var s = media.Statistics;
        return new DecoderLossCounters(s.LostPictures, s.DemuxCorrupted, s.DemuxDiscontinuity);
    }

    // SP-0070: the two counters the freeze rule differences, from the same statistics LogStats prints and
    // under the same Media-wrapper discipline - the getter retains the native media on every call, and
    // this runs on the watchdog's 3 s tick, so an undisposed wrapper would leak a media per sample.
    // DemuxReadBytes, not ReadBytes: as documented on Rate above, libvlc fills the access-side counter
    // only, and the HLS/DASH demuxers fetch their segments below it - so ReadBytes sits frozen for a
    // whole healthy .m3u8 session and would read as "no data arriving" on every adaptive stream.
    public PlaybackProgressCounters? ReadProgressCounters()
    {
        if (_disposed)
        {
            return null;
        }

        using var media = _mediaPlayer.Media;
        if (media is null)
        {
            return null;
        }

        var s = media.Statistics;
        return new PlaybackProgressCounters(s.DisplayedPictures, (long)s.DemuxReadBytes);
    }

    // Same Media-wrapper discipline as LogStats: the getter retains the native media on every call.
    public StreamTransmission? DescribeTransmission()
    {
        if (_disposed)
        {
            return null;
        }

        using var media = _mediaPlayer.Media;
        return media is null ? null : StreamTransmissionProbe.Read(media);
    }

    // SP-0073: the same Media-wrapper discipline as LogStats - the getter retains the native media on
    // every call, and this runs on the two-second stats tick, so an undisposed wrapper would leak one
    // media per sample.
    //
    // NowPlaying only, deliberately. libvlc also carries MetadataType.Title, but on an HTTP source that
    // is routinely the URL or its last path segment, so consulting it as a fallback would put a link
    // under the channel name and read as a defect. NowPlaying is the field that means "what is on air":
    // libvlc fills it from ICY StreamTitle on a radio stream and from the current programme on a source
    // that announces one, which is exactly the two cases the ticket names.
    public string? ReadNowPlaying()
    {
        if (_disposed)
        {
            return null;
        }

        using var media = _mediaPlayer.Media;
        return media?.Meta(MetadataType.NowPlaying);
    }

    /// <summary>SP-0077: the rendition on screen - the newest video ES this media has opened.</summary>
    /// <remarks>
    /// <para>Indirect on purpose. The two APIs that look like the answer were measured and both lie on an
    /// adaptive stream: <c>MediaPlayer.VideoTrack</c> stays at -1 for the whole session, and
    /// <c>MediaPlayer.Size</c> keeps returning the resolution the vout was first built with while the
    /// engine plays something else entirely - and returns <c>true</c> while doing it. The media's own ES
    /// list is a <em>history</em> with ascending ids, so its highest-numbered video track is the one most
    /// recently started, which is the one being shown; never the first entry and never the widest, as
    /// both name a rendition that may have stopped playing minutes ago. Evidence and the one case where
    /// this rule would be wrong (two video ES opened together, only one played): `docs/PLAYBACK_RESILIENCE.md`
    /// §5 and `temp/SP-0077/OBSERVATION.md`.</para>
    /// <para>Same Media-wrapper discipline as <see cref="LogStats"/>: the getter retains the native media
    /// on every call, and this runs on the two-second stats tick, so an undisposed wrapper would leak one
    /// media per sample.</para>
    /// </remarks>
    public VideoRendition? ReadRendition()
    {
        if (_disposed)
        {
            return null;
        }

        using var media = _mediaPlayer.Media;
        if (media is null)
        {
            return null;
        }

        var newest = -1;
        VideoRendition? playing = null;
        foreach (var track in media.Tracks)
        {
            if (track.TrackType != TrackType.Video || track.Id <= newest)
            {
                continue;
            }

            newest = track.Id;
            playing = new VideoRendition((int)track.Data.Video.Width, (int)track.Data.Video.Height);
        }

        return playing;
    }

    private static VideoTrack[] Describe(TrackDescription[]? tracks) =>
        tracks?.Where(track => track.Id >= 0).Select(track => new VideoTrack(track.Id, track.Name)).ToArray() ?? [];

    private void MediaPlayer_SnapshotTaken(object? sender, MediaPlayerSnapshotTakenEventArgs e)
    {
        var frame = LoadFrozenImage(e.Filename);
        _log.Event("THUMB TAKEN", $"loaded={frame is not null}", $"url={_lastUrl}");
        TryDeleteFile(e.Filename);
        if (frame is not null)
        {
            SnapshotReady?.Invoke(frame);
        }
    }

    private static BitmapSource? LoadFrozenImage(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bgr = new FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgr32, null, 0);
            // Copy into an independent raw-pixel BitmapSource so it is safe to JPEG-encode on a worker thread.
            var width = bgr.PixelWidth;
            var height = bgr.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bgr.CopyPixels(pixels, stride, 0);
            var frame = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
            frame.Freeze();
            return frame;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp snapshot is harmless; the OS reclaims the temp folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a locked temp file is not worth surfacing.
        }
    }

    // Log-only handlers run on VLC threads; CurrentLog is thread-safe, so no dispatcher hop is needed.
    private void MediaPlayer_Opening(object? sender, EventArgs e) => _log.Event("STATE OPENING", $"url={_lastUrl}");

    private void MediaPlayer_Playing(object? sender, EventArgs e) => _log.Event("STATE PLAYING", $"url={_lastUrl}");

    private void MediaPlayer_Paused(object? sender, EventArgs e) => _log.Event("STATE PAUSED", $"url={_lastUrl}");

    private void MediaPlayer_Stopped(object? sender, EventArgs e) => _log.Event("STATE STOPPED", $"url={_lastUrl}");

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        _log.Event("STATE END_REACHED", $"url={_lastUrl}");
        EndReached?.Invoke();
    }

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) => EncounteredError?.Invoke();

    private void MediaPlayer_TracksChanged(object? sender, EventArgs e) => TracksChanged?.Invoke();

    private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e) => BufferingChanged?.Invoke(e.Cache);

    private void LibVlc_Log(object? sender, LogEventArgs e)
    {
        if (e.Level is LogLevel.Warning or LogLevel.Error)
        {
            _log.Event("VLC", $"level={e.Level}", $"module={e.Module}", $"msg={e.Message}");
        }
    }
}
