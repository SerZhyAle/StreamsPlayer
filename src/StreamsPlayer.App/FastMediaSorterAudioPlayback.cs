using System.Diagnostics;
using LibVLCSharp.Shared;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// LibVLC audio output over one already-opened FastMediaSorter HTTP response.
/// Passing the response stream through <see cref="StreamMediaInput"/> prevents LibVLC from opening a
/// second HTTP connection just to discover the media or its status.
/// </summary>
internal sealed class FastMediaSorterAudioPlayback : IDisposable
{
    internal const int BufferTargetMilliseconds = 200;

    // SP-0165: how long the retirement worker waits for the native stop before it abandons the engine.
    private static readonly TimeSpan NativeStopTimeout = StandardAudioPlayback.NativeStopTimeout;

    // One engine for every leg: creating a LibVLC instance loads its plugin set, which is time taken out
    // of the one-second open budget on each reconnect for no benefit. It lives as long as the process.
    private static readonly Lazy<LibVLC> SharedLibVlc = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-video", "--no-video-title-show", "--no-osd", "--quiet", "--clock-jitter=0");
    });

    private readonly FastMediaSorterPlaybackTransport _transport = new();
    // SP-0120: this leg's own lifetime. The caller's token is the whole listening session's, so an abandoned leg
    // - superseded by a reconnect, or stopped while its request was out - used to leave that request open, with
    // no timeout, until the session ended. Dispose cancels it; StartAsync links it into the request.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _openStopwatch = new();
    private MediaPlayer? _player;
    private Media? _media;
    private StreamMediaInput? _input;
    private FastMediaSorterPlaybackConnection? _connection;
    private bool _disposed;
    // SP-0165 (shared rule with StandardAudioPlayback): engines abandoned to stops that never returned are counted,
    // and past the cap the feature pauses for the rest of the session. A leg is a new instance on every reconnect,
    // so the count lives on the type: a per-instance budget could never pass one engine.
    private static readonly AbandonedEngineBudget Abandoned = new();
    private readonly Action<string, string[]>? _diagnostics;
    private string? _audioOutputDevice;
    private AudioChannelMode _audioChannelMode = AudioChannelMode.Stereo;

    public FastMediaSorterAudioPlayback(Action<string, string[]>? diagnostics = null)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>The output device chosen in Settings; null or empty keeps the system default. Applied to the leg playing now and to every later one.</summary>
    public string? AudioOutputDevice
    {
        get => _audioOutputDevice;
        set
        {
            _audioOutputDevice = value;
            if (_player is { } player && !string.IsNullOrEmpty(value))
            {
                ApplyOutputDevice(player, value);
            }
        }
    }

    /// <summary>The channel routing chosen in Settings. Applied to the leg playing now and to every later one.</summary>
    public AudioChannelMode AudioChannelMode
    {
        get => _audioChannelMode;
        set
        {
            _audioChannelMode = value;
            if (_player is { } player)
            {
                ApplyChannelMode(player, value);
            }
        }
    }

    public event EventHandler<FastMediaSorterAudioPlaybackEventArgs>? Playing;
    public event EventHandler<FastMediaSorterAudioPlaybackEventArgs>? Ended;
    public event EventHandler<FastMediaSorterAudioPlaybackEventArgs>? Failed;

    public int? ResponseStatusCode => _connection?.StatusCode;

    public async Task<FastMediaSorterAudioOpenResult> StartAsync(Uri endpoint, int volume, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        StopPlayback();
        if (Abandoned.IsPaused)
        {
            // SP-0165: past the abandonment cap this feature pauses for the session. The refusal is an ordinary
            // failed open, which the owner's recovery budget turns into an ended session rather than a retry loop.
            _diagnostics?.Invoke("AUDIO PLAY REFUSED", ["route=fastmediasorter", "reason=abandoned_engine_cap"]);
            return new FastMediaSorterAudioOpenResult(
                null,
                TimeSpan.Zero,
                new InvalidOperationException("FastMediaSorter audio is paused for this session after native stops that never returned."),
                Cancelled: false);
        }

        _openStopwatch.Restart();
        FastMediaSorterPlaybackConnection connection;
        using (var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
        {
            connection = await _transport.OpenAsync(endpoint, request.Token);
        }

        // Disposed while the request was out: the owner has already moved on, and starting an engine
        // here would leave sound playing that nothing can stop.
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            connection.Dispose();
            return new FastMediaSorterAudioOpenResult(null, connection.ResponseElapsed, null, Cancelled: true);
        }

        if (connection.Stream is null)
        {
            var result = new FastMediaSorterAudioOpenResult(connection.StatusCode, connection.ResponseElapsed, connection.TransportError, Cancelled: false);
            connection.Dispose();
            return result;
        }

        _connection = connection;
        try
        {
            var libVlc = SharedLibVlc.Value;
            _player = new MediaPlayer(libVlc) { Volume = volume };
            if (!string.IsNullOrEmpty(_audioOutputDevice))
            {
                ApplyOutputDevice(_player, _audioOutputDevice);
            }

            ApplyChannelMode(_player, _audioChannelMode);
            _player.Playing += Player_Playing;
            _player.EndReached += Player_EndReached;
            _player.EncounteredError += Player_EncounteredError;
            _input = new StreamMediaInput(connection.Stream);
            _media = new Media(
                libVlc,
                _input,
                $":file-caching={BufferTargetMilliseconds}",
                $":network-caching={BufferTargetMilliseconds}",
                $":live-caching={BufferTargetMilliseconds}",
                ":clock-jitter=0");
            if (!_player.Play(_media))
            {
                var error = new InvalidOperationException("LibVLC rejected FastMediaSorter audio playback.");
                StopPlayback();
                return new FastMediaSorterAudioOpenResult(connection.StatusCode, connection.ResponseElapsed, error, Cancelled: false);
            }

            return new FastMediaSorterAudioOpenResult(connection.StatusCode, connection.ResponseElapsed, null, Cancelled: false);
        }
        catch (Exception ex)
        {
            StopPlayback();
            return new FastMediaSorterAudioOpenResult(connection.StatusCode, connection.ResponseElapsed, ex, Cancelled: false);
        }
    }

    public void SetVolume(int volume)
    {
        if (_player is not null)
        {
            _player.Volume = volume;
        }
    }

    public void StopPlayback()
    {
        var player = _player;
        _player = null;
        if (player is not null)
        {
            player.Playing -= Player_Playing;
            player.EndReached -= Player_EndReached;
            player.EncounteredError -= Player_EncounteredError;
        }

        // The connection goes first. LibVLC's input thread may be blocked inside a read of a stalled
        // socket, and MediaPlayer.Stop waits for that thread - closing the response is what unblocks it,
        // so the other order can hang the UI thread for as long as the network stays silent.
        _connection?.Dispose();
        _connection = null;
        if (player is not null)
        {
            // Route the native Stop and Dispose through the retire-under-deadline pattern to prevent
            // hanging the UI thread on a flapping stream.
            _ = RetirePlayerAsync(player);
        }

        _media?.Dispose();
        _media = null;
        _input?.Dispose();
        _input = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel(); // ends a request still out; StartAsync then reports the leg as cancelled
        _lifetime.Dispose(); // StartAsync links it before its first await, on this same thread, so nothing links it later
        StopPlayback();
    }

    private void Player_Playing(object? sender, EventArgs e) =>
        Playing?.Invoke(this, new FastMediaSorterAudioPlaybackEventArgs(ResponseStatusCode, _openStopwatch.Elapsed, _connection?.TransportError));

    private void Player_EndReached(object? sender, EventArgs e) =>
        Ended?.Invoke(this, new FastMediaSorterAudioPlaybackEventArgs(ResponseStatusCode, _openStopwatch.Elapsed, _connection?.TransportError));

    private void Player_EncounteredError(object? sender, EventArgs e) =>
        Failed?.Invoke(this, new FastMediaSorterAudioPlaybackEventArgs(ResponseStatusCode, _openStopwatch.Elapsed, _connection?.TransportError));

    private void ApplyOutputDevice(MediaPlayer player, string deviceId)
    {
        try
        {
            player.SetOutputDevice(deviceId);
        }
        catch (Exception exception)
        {
            _diagnostics?.Invoke("AUDIO DEVICE REFUSED", ["route=fastmediasorter", $"error={exception.GetType().Name}"]);
        }
    }

    private void ApplyChannelMode(MediaPlayer player, AudioChannelMode mode)
    {
        try
        {
            player.SetChannel(mode.ToLibVlc());
        }
        catch (Exception exception)
        {
            _diagnostics?.Invoke("AUDIO CHANNEL MODE REFUSED", ["route=fastmediasorter", $"error={exception.GetType().Name}"]);
        }
    }

    private async Task RetirePlayerAsync(MediaPlayer player)
    {
        var stop = Task.Run(() => player.Stop());
        try
        {
            await stop.WaitAsync(NativeStopTimeout);
        }
        catch (TimeoutException)
        {
            var pausedNow = Abandoned.RecordAbandoned();
            _diagnostics?.Invoke("AUDIO ENGINE ABANDONED", ["route=fastmediasorter", $"outstanding={Abandoned.Outstanding}", $"cap={AbandonedEngineBudget.DefaultCap}"]);
            if (pausedNow)
            {
                _diagnostics?.Invoke("AUDIO PLAYBACK PAUSED", ["route=fastmediasorter", "reason=native_stop_hung", $"abandoned={Abandoned.Outstanding}", "until=session_end"]);
            }

            _ = stop.ContinueWith(
                completed =>
                {
                    _ = completed.Exception; // a failed stop still ends in the release below
                    ReleaseContained(player);
                    Abandoned.RecordReleased();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return;
        }
        catch (Exception exception)
        {
            // The stop itself faulted. The release below still runs: a player that failed to stop is no reason
            // to keep its native threads and buffers for the rest of the session.
            _diagnostics?.Invoke("AUDIO ENGINE STOP FAULT", ["route=fastmediasorter", $"error={exception.GetType().Name}"]);
        }

        ReleaseContained(player);
    }

    private void ReleaseContained(MediaPlayer player)
    {
        try
        {
            player.Dispose();
        }
        catch (Exception exception)
        {
            _diagnostics?.Invoke("AUDIO ENGINE DISPOSE FAULT", ["route=fastmediasorter", $"error={exception.GetType().Name}"]);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed record FastMediaSorterAudioOpenResult(
    int? StatusCode,
    TimeSpan ResponseElapsed,
    Exception? Error,
    bool Cancelled)
{
    public bool Started => !Cancelled && Error is null && StatusCode is >= 200 and < 300;
}

internal sealed class FastMediaSorterAudioPlaybackEventArgs(int? statusCode, TimeSpan openToPlaying, Exception? transportError) : EventArgs
{
    public int? StatusCode { get; } = statusCode;
    public TimeSpan OpenToPlaying { get; } = openToPlaying;
    public Exception? TransportError { get; } = transportError;
}
