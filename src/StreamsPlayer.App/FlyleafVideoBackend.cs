using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026 FlyleafLib (FFmpeg/DirectX) implementation of <see cref="IVideoBackend"/> — the opt-in,
/// experimental alternative engine. Maps the seam onto FlyleafLib 3.10.x. LibVLC remains the default.
/// </summary>
/// <remarks>
/// Experimental caveats (surfaced by the Settings "experimental" label, never as a crash):
/// the FFmpeg v8 native binaries are NOT delivered by NuGet — they must be deployed (x64 only) into
/// the folder resolved by <see cref="ResolveFFmpegPath"/>. If they are absent (or on win-arm64), the
/// constructor throws during engine start and <see cref="VideoBackendFactory"/> falls back to LibVLC.
/// FlyleafLib has no LibVLC-style live-statistics surface, so <see cref="LogStats"/> is a no-op.
/// </remarks>
internal sealed class FlyleafVideoBackend : IVideoBackend
{
    private static bool _engineStarted;
    private static readonly object EngineLock = new();

    private readonly Player _player;
    private readonly Config _config;
    private readonly FlyleafHost _host;
    private readonly CurrentLog _log;
    private string _lastUrl = string.Empty;
    private bool _reachedPlaying;
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
        _player.Audio.Volume = Math.Clamp(volume, 0, 100);
        _player.Audio.Mute = muted;
        _player.BufferingStarted += OnBufferingStarted;
        _player.BufferingCompleted += OnBufferingCompleted;
        _player.OpenCompleted += OnOpenCompleted;
        _player.PlaybackStopped += OnPlaybackStopped;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        _host = new FlyleafHost { Player = _player };
    }

    public FrameworkElement View => _host;

    // FlyleafHost is a ContentControl whose Content is presented on the overlay window above the
    // DirectX video surface; assigning it keeps the control panel on top of the video through resizes.
    public void SetOverlay(FrameworkElement overlay) => _host.Content = overlay;

    public long PositionMs => _player.CurTime / TimeSpan.TicksPerMillisecond;

    public bool IsPlaying => _player.IsPlaying;

    public int Volume { set => _player.Audio.Volume = Math.Clamp(value, 0, 100); }

    public bool Mute { set => _player.Audio.Mute = value; }

    public event Action<float>? BufferingChanged;
    public event Action? EndReached;
    public event Action? EncounteredError;
    public event Action? TracksChanged;
    public event Action<BitmapSource>? SnapshotReady;

    public bool Play(Uri url, uint cacheMilliseconds, bool rtspOverTcp, bool softwareDecode)
    {
        _reachedPlaying = false;
        _selectedAudioPos = -1;
        _selectedSubtitlePos = -1;
        _config.Video.VideoAcceleration = !softwareDecode; // force software decode when requested
        _config.Demuxer.BufferDuration = (long)cacheMilliseconds * TimeSpan.TicksPerMillisecond;
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
    public void LogStats(string tag)
    {
    }

    private static VideoTrack[] Map<T>(IEnumerable<T> streams) =>
        streams.Select((stream, index) => new VideoTrack(index, stream?.ToString())).ToArray();

    private void OnBufferingStarted(object? sender, EventArgs e) => BufferingChanged?.Invoke(0f);

    private void OnBufferingCompleted(object? sender, BufferingCompletedArgs e)
    {
        if (e.Success)
        {
            BufferingChanged?.Invoke(100f);
        }
        else
        {
            EncounteredError?.Invoke();
        }
    }

    private void OnOpenCompleted(object? sender, OpenCompletedArgs e)
    {
        if (e.Success)
        {
            TracksChanged?.Invoke(); // embedded streams are populated after a successful open
        }
        else
        {
            EncounteredError?.Invoke();
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStoppedArgs e)
    {
        if (e.Success)
        {
            EndReached?.Invoke(); // reached end of a live stream -> bounded recovery re-anchors to the live edge
        }
        else
        {
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

            var ffmpegPath = ResolveFFmpegPath();
            log.Event("FLYLEAF ENGINE", "action=start", $"ffmpeg_path={ffmpegPath}");
            Engine.Start(new EngineConfig
            {
                FFmpegPath = ffmpegPath,
                UIRefresh = false
            });
            _engineStarted = true;
        }
    }

    // FlyleafLib's default is "FFmpeg" relative to the app base directory; the native FFmpeg v8 DLLs
    // (x64) must be deployed there. Missing natives make Engine.Start throw -> LibVLC fallback.
    private static string ResolveFFmpegPath() => Path.Combine(AppContext.BaseDirectory, "FFmpeg");
}
