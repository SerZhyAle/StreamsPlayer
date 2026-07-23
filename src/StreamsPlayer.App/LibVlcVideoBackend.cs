using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026 LibVLC implementation of <see cref="IVideoBackend"/> — the proven default engine.
/// This is a behaviour-preserving extraction of the LibVLC surface that previously lived directly
/// in <see cref="PlayerWindow"/>: the exact option set, per-media caching, snapshot pipeline, track
/// enumeration, live statistics, and the Play/teardown race protection (<see cref="_mediaGate"/>).
/// </summary>
internal sealed class LibVlcVideoBackend : IVideoBackend
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly LibVLCSharp.WPF.VideoView _videoView;
    private readonly CurrentLog _log;
    // Serializes native Play against Stop/Dispose so a background reconnect can never race teardown.
    private readonly object _mediaGate = new();
    private Media? _media;
    private string _lastUrl = string.Empty;
    private bool _disposed;

    public LibVlcVideoBackend(int volume, bool muted, CurrentLog log)
    {
        _log = log;
        LibVLCSharp.Shared.Core.Initialize();
        // clock-jitter=0 tolerates PCR/PTS jitter; avcodec-hw=none forces software decode to avoid GPU surface starvation.
        // Drop late frames to keep playback alive (steady ~17 fps, no long black screens); the freeze watchdog reconnects
        // if the pipeline fully deadlocks. (--no-drop-late-frames / --no-ts-trust-pcr tried and reverted: they deadlock.)
        _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--rtsp-tcp", "--clock-jitter=0", "--avcodec-hw=none");
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

    public long PositionMs => _mediaPlayer.Time;

    public bool IsPlaying => _mediaPlayer.State == VLCState.Playing;

    public int Volume { set => _mediaPlayer.Volume = value; }

    public bool Mute { set => _mediaPlayer.Mute = value; }

    public event Action<float>? BufferingChanged;
    public event Action? EndReached;
    public event Action? EncounteredError;
    public event Action? TracksChanged;
    public event Action<BitmapSource>? SnapshotReady;

    public bool Play(Uri url, uint cacheMilliseconds, bool rtspOverTcp, bool softwareDecode)
    {
        lock (_mediaGate)
        {
            if (_disposed)
            {
                return false; // backend is tearing down; do not touch the (soon) disposed player
            }

            _lastUrl = url.ToString();
            _mediaPlayer.NetworkCaching = cacheMilliseconds;
            var previous = _media;
            _media = new Media(_libVlc, url);
            _media.AddOption($":network-caching={cacheMilliseconds}");
            _media.AddOption($":live-caching={cacheMilliseconds}");
            if (rtspOverTcp)
            {
                _media.AddOption(":rtsp-tcp");
            }

            if (softwareDecode)
            {
                _media.AddOption(":avcodec-hw=none");
            }

            var started = _mediaPlayer.Play(_media);
            previous?.Dispose();
            return started;
        }
    }

    public Task StopAndDisposeAsync()
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
        return Task.Run(() =>
        {
            lock (_mediaGate)
            {
                _disposed = true;
                mediaPlayer.Stop();
                _media?.Dispose();
                _media = null;
                mediaPlayer.Dispose();
                libVlc.Dispose();
            }
        });
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

    // Input/demux counters distinguish network starvation (read_bytes frozen) from decode loss (lost_pics rising).
    public void LogStats(string tag)
    {
        var media = _mediaPlayer.Media;
        if (media is null)
        {
            return;
        }

        var s = media.Statistics;
        _log.Event(tag,
            $"read_bytes={s.ReadBytes}",
            $"in_bitrate={s.InputBitrate:F4}",
            $"demux_bytes={s.DemuxReadBytes}",
            $"demux_bitrate={s.DemuxBitrate:F4}",
            $"decoded_v={s.DecodedVideo}",
            $"displayed={s.DisplayedPictures}",
            $"lost_pics={s.LostPictures}",
            $"corrupted={s.DemuxCorrupted}",
            $"discont={s.DemuxDiscontinuity}",
            $"url={_lastUrl}");
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
