using System;
using LibVLCSharp.Shared;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// LibVLC-based audio playback engine for standard radio streams.
/// Replaces WPF <see cref="System.Windows.Controls.MediaElement"/> to avoid CAS/pack URI restrictions
/// on HTTP/HTTPS audio streams in modern .NET environments.
/// </summary>
/// <remarks>
/// SP-0120: one instance serves every station for the whole session, so its events used to say nothing about
/// which connection raised them - and the owner handles them later, on the UI thread. Each connection is now
/// stamped when it opens, every event carries the stamp, and <see cref="IsCurrent"/> tells the owner whether the
/// connection that raised an event is still the one playing.
/// </remarks>
internal sealed class StandardAudioPlayback : IDisposable
{
    private static readonly Lazy<LibVLC> SharedLibVlc = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-video", "--no-video-title-show", "--no-osd", "--quiet", "--clock-jitter=0");
    });

    internal static LibVLC SharedLibVlcInstance => SharedLibVlc.Value;

    /// <summary>
    /// SP-0165: how long the retirement worker waits for the native stop before it abandons the engine. The
    /// same measured bound the preview captures hold their stops to (SP-0120): LibVLC's stop blocks on a
    /// flapping stream, and no radio stop may hold anything hostage for longer.
    /// </summary>
    internal static readonly TimeSpan NativeStopTimeout = TimeSpan.FromSeconds(2);

    private readonly PlaybackConnectionSequence _connections = new();
    private Leg? _leg;
    private Media? _media;
    private bool _disposed;
    private string? _audioOutputDevice;
    private AudioChannelMode _audioChannelMode = AudioChannelMode.Stereo;

    public string? AudioOutputDevice
    {
        get => _audioOutputDevice;
        set
        {
            _audioOutputDevice = value;
            if (_leg?.Player is { } player && !string.IsNullOrEmpty(value))
            {
                try
                {
                    player.SetOutputDevice(value);
                }
                catch { }
            }
        }
    }

    public AudioChannelMode AudioChannelMode
    {
        get => _audioChannelMode;
        set
        {
            _audioChannelMode = value;
            if (_leg?.Player is { } player)
            {
                try
                {
                    player.SetChannel(value.ToLibVlc());
                }
                catch { }
            }
        }
    }

    // SP-0165: where the abandonment of a hung engine is told; null keeps the feature silent as before.
    private readonly Action<string, string[]>? _diagnostics;
    // SP-0165 (shared rule with SP-0166 R3): engines abandoned to stops that never returned are counted,
    // and past the cap the feature pauses for the rest of the session.
    private readonly AbandonedEngineBudget _abandoned = new();
    // SP-0189: what the engine said about the connection now open, so its failure is logged with a cause. Fed by the
    // shared instance's log on engine threads, cleared as each connection opens, read when that connection fails.
    private readonly object _logFailureTrailGate = new();
    private readonly EngineFailureTrail _logFailureTrail = new();
    private LibVLC? _engineLog;

    public StandardAudioPlayback(Action<string, string[]>? diagnostics = null, AbandonedEngineBudget? abandoned = null)
    {
        _diagnostics = diagnostics;
        if (abandoned is not null)
        {
            _abandoned = abandoned;
        }
    }

    public event EventHandler<StandardAudioEventArgs>? Playing;
    public event EventHandler<StandardAudioEventArgs>? Ended;
    public event EventHandler<StandardAudioFailedEventArgs>? Failed;

    public bool IsPlaying => _leg?.Player.IsPlaying ?? false;

    /// <summary>Whether an event stamped with <paramref name="connection"/> comes from the connection playing now.</summary>
    public bool IsCurrent(PlaybackConnectionId connection) => _connections.IsCurrent(connection);

    public void Play(Uri uri, int volume)
    {
        ThrowIfDisposed();
        StopPlayback();
        if (_abandoned.IsPaused)
        {
            // SP-0165: past the abandonment cap this engine pauses for the session. The refusal rides the
            // ordinary failure funnel - a current connection and a Failed event - which is what tells the
            // user and, through the recovery budget, ends the session instead of retrying for ever.
            var refused = _connections.Open();
            _diagnostics?.Invoke("AUDIO PLAY REFUSED", ["reason=abandoned_engine_cap"]);
            Failed?.Invoke(this, StandardAudioFailedEventArgs.Local(
                refused, StandardAudioFailedEventArgs.PlayRejectedReason, "abandoned_engine_cap"));
            return;
        }

        var connection = _connections.Open();
        ForgetEngineErrors();

        try
        {
            var libVlc = SharedLibVlc.Value;
            ListenToEngineLog(libVlc);
            _leg = new Leg(this, connection, new MediaPlayer(libVlc) { Volume = volume });
            if (!string.IsNullOrEmpty(_audioOutputDevice))
            {
                try
                {
                    _leg.Player.SetOutputDevice(_audioOutputDevice);
                }
                catch { }
            }
            _leg.Player.SetChannel(_audioChannelMode.ToLibVlc());
            _media = new Media(libVlc, uri, ":clock-jitter=0");
            if (!_leg.Player.Play(_media))
            {
                ReleaseLeg();
                Failed?.Invoke(this, StandardAudioFailedEventArgs.Local(
                    connection, StandardAudioFailedEventArgs.PlayRejectedReason, DescribeEngineErrors()));
            }
        }
        catch (Exception ex)
        {
            // Released but still current: the owner handles this failure after it returns, and a failure nobody
            // may act on would leave the station connecting for ever. Its StopPlayback is what ends the identity.
            // The engine, its player or its media could not be created: local, whatever the exception is.
            ReleaseLeg();
            Failed?.Invoke(this, StandardAudioFailedEventArgs.Local(connection, ex.GetType().Name, FaultLogFields.TextOf(ex)));
        }
    }

    /// <summary>
    /// SP-0189: listens to the shared engine's log once, for the life of this instance. The video backend keeps the
    /// same subscription on its own engine for whole sessions; a playing station logs a few dozen lines a second.
    /// </summary>
    private void ListenToEngineLog(LibVLC libVlc)
    {
        if (_engineLog is not null)
        {
            return;
        }

        _engineLog = libVlc;
        libVlc.Log += EngineLog_Logged;
    }

    // Engine threads. Only error-level lines ever explained a failed open in the SP-0189 research, so everything
    // else - nearly every line - returns before the lock.
    private void EngineLog_Logged(object? sender, LogEventArgs e)
    {
        if (e.Level != LogLevel.Error)
        {
            return;
        }

        lock (_logFailureTrailGate)
        {
            _logFailureTrail.Observe(e.Module, e.Message);
        }
    }

    private void ForgetEngineErrors()
    {
        lock (_logFailureTrailGate)
        {
            _logFailureTrail.Clear();
        }
    }

    private string? DescribeEngineErrors()
    {
        lock (_logFailureTrailGate)
        {
            return _logFailureTrail.Describe();
        }
    }

    /// <summary>
    /// SP-0133: what <paramref name="connection"/> has actually produced - decoded audio blocks and buffers the audio
    /// output played - or null once it is no longer the connection playing. The engine reports Playing before a
    /// single sample is decoded, so this is the only evidence that sound came out.
    /// </summary>
    public AudioOutputCounters? ReadOutput(PlaybackConnectionId connection)
    {
        if (_disposed || !_connections.IsCurrent(connection) || _media is not { } media)
        {
            return null;
        }

        var stats = media.Statistics;
        return new AudioOutputCounters(stats.DecodedAudio, stats.PlayedAudioBuffers, stats.LostAudioBuffers);
    }

    /// <summary>
    /// SP-0169: the bytes <paramref name="connection"/> has read from its source so far - the access layer's count
    /// plus the demuxer's, so a stream delivered either way moves it - or null once it is no longer the connection
    /// playing. A monotonic total, which is what lets a stall be seen as it stopping growing.
    /// </summary>
    public long? ReadInputBytes(PlaybackConnectionId connection)
    {
        if (_disposed || !_connections.IsCurrent(connection) || _media is not { } media)
        {
            return null;
        }

        var stats = media.Statistics;
        return (long)stats.ReadBytes + (long)stats.DemuxReadBytes;
    }

    public void SetVolume(int volume)
    {
        if (_leg is { } leg)
        {
            leg.Player.Volume = volume;
        }
    }

    public void Pause()
    {
        if (_leg is { Player.IsPlaying: true } leg)
        {
            leg.Player.Pause();
        }
    }

    public void Resume()
    {
        if (_leg is { Player.IsPlaying: false } leg)
        {
            leg.Player.Play();
        }
    }

    /// <summary>
    /// Stops the connection; an event it raised that has not been handled yet is no longer current.
    /// </summary>
    /// <remarks>
    /// SP-0165: the identity closes and the leg's events unhook here - cheap, and what keeps a queued event
    /// from being handled as current. The native stop and release used to follow on the calling thread, and
    /// every radio stop, switch, failure, end, open timeout and close funnels through here, so LibVLC's stop
    /// blocking on a flapping stream froze the UI thread each time. They run on the pool now, under
    /// <see cref="NativeStopTimeout"/>; a stop that does not return abandons the engine to its late
    /// continuation and counts it.
    /// </remarks>
    public void StopPlayback()
    {
        _connections.Close();
        ReleaseLeg();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPlayback();
        if (_engineLog is { } engineLog)
        {
            engineLog.Log -= EngineLog_Logged;
            _engineLog = null;
        }
    }

    private void ReleaseLeg()
    {
        var leg = _leg;
        _leg = null;
        leg?.Detach(); // before any await, so nothing the old leg raises is ever handled as current
        _media?.Dispose();
        _media = null;
        if (leg is not null)
        {
            _ = Task.Run(() => RetireAsync(leg));
        }
    }

    /// <summary>
    /// Stops and releases one retired leg off the calling thread, under a deadline.
    /// </summary>
    /// <remarks>
    /// A stop that outlives its bound abandons the player to its own late release: whoever the hung stop
    /// finally returns to disposes it and lowers the count - exactly the pattern the preview captures use.
    /// The late continuation and this method's normal path both release through <see cref="ReleaseContained"/>,
    /// because a release fault costs a log line, never the session.
    /// </remarks>
    private async Task RetireAsync(Leg leg)
    {
        var stop = Task.Run(leg.Stop);
        try
        {
            await stop.WaitAsync(NativeStopTimeout);
        }
        catch (TimeoutException)
        {
            var pausedNow = _abandoned.RecordAbandoned();
            _diagnostics?.Invoke("AUDIO ENGINE ABANDONED", [$"outstanding={_abandoned.Outstanding}", $"cap={AbandonedEngineBudget.DefaultCap}"]);
            if (pausedNow)
            {
                _diagnostics?.Invoke("AUDIO PLAYBACK PAUSED", ["reason=native_stop_hung", $"abandoned={_abandoned.Outstanding}", "until=session_end"]);
            }

            _ = stop.ContinueWith(
                completed =>
                {
                    _ = completed.Exception; // a failed stop still ends in the release below
                    ReleaseContained(leg);
                    _abandoned.RecordReleased();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return;
        }

        ReleaseContained(leg);
    }

    /// <summary>A release fault costs a log line. Runs on the retirement worker or its continuation, never the UI thread.</summary>
    private void ReleaseContained(Leg leg)
    {
        try
        {
            leg.Release();
        }
        catch (Exception exception)
        {
            _diagnostics?.Invoke("AUDIO ENGINE RELEASE FAULT", FaultLogFields.Of(exception));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// One connection's player and the handlers that stamp its identity into what it raises. The handlers are
    /// per leg, not per instance, because an engine thread can be inside a handler of a player that has already
    /// been replaced - only a stamp captured when that player was created can still name it correctly.
    /// </summary>
    private sealed class Leg
    {
        private readonly EventHandler<EventArgs> _playing;
        private readonly EventHandler<EventArgs> _ended;
        private readonly EventHandler<EventArgs> _error;

        public Leg(StandardAudioPlayback owner, PlaybackConnectionId connection, MediaPlayer player)
        {
            Player = player;
            _playing = (_, _) => owner.Playing?.Invoke(owner, new StandardAudioEventArgs(connection));
            _ended = (_, _) => owner.Ended?.Invoke(owner, new StandardAudioEventArgs(connection));
            // SP-0189: the engine logs its cause before it raises this event, so the trail already holds it.
            _error = (_, _) => owner.Failed?.Invoke(owner, StandardAudioFailedEventArgs.EngineError(
                connection, owner.DescribeEngineErrors()));
            player.Playing += _playing;
            player.EndReached += _ended;
            player.EncounteredError += _error;
        }

        public MediaPlayer Player { get; }

        /// <summary>Unhooks this leg's events. Runs on the calling thread as the first act of a stop (SP-0120 identity rule).</summary>
        public void Detach()
        {
            Player.Playing -= _playing;
            Player.EndReached -= _ended;
            Player.EncounteredError -= _error;
        }

        /// <summary>The blocking native stop. Runs on the retirement worker only - never the calling thread (SP-0165).</summary>
        public void Stop() => Player.Stop();

        /// <summary>The native release: the stop again, then the disposal. Runs on the retirement worker or its late continuation.</summary>
        public void Release()
        {
            Stop();
            Player.Dispose();
        }
    }
}

internal class StandardAudioEventArgs(PlaybackConnectionId connection) : EventArgs
{
    /// <summary>The connection that raised the event (SP-0120); ask <see cref="StandardAudioPlayback.IsCurrent"/>.</summary>
    public PlaybackConnectionId Connection { get; } = connection;
}

/// <summary>
/// A failed connection, as the engine reported it (SP-0189) - never an exception made up to carry the news: the
/// recovery classifier used to read that exception's type name as the cause, and judged every network failure final.
/// </summary>
internal sealed class StandardAudioFailedEventArgs : StandardAudioEventArgs
{
    /// <summary>The engine's error event. Every one seen in the SP-0189 research was an open the network or the server refused.</summary>
    public const string EngineErrorReason = "encountered_error";

    /// <summary>The engine refused to start the connection at all.</summary>
    public const string PlayRejectedReason = "play_rejected";

    private StandardAudioFailedEventArgs(PlaybackConnectionId connection, string reason, string? cause, bool localEngineFailure)
        : base(connection)
    {
        Reason = reason;
        Cause = cause;
        LocalEngineFailure = localEngineFailure;
    }

    /// <summary>The token the recovery and report classifiers read.</summary>
    public string Reason { get; }

    /// <summary>What the engine said about the failure, as one bounded line, or null when it said nothing. For the log only.</summary>
    public string? Cause { get; }

    /// <summary>Established here: the engine itself could not start, so no re-open of the stream can succeed.</summary>
    public bool LocalEngineFailure { get; }

    public static StandardAudioFailedEventArgs EngineError(PlaybackConnectionId connection, string? cause) =>
        new(connection, EngineErrorReason, cause, localEngineFailure: false);

    public static StandardAudioFailedEventArgs Local(PlaybackConnectionId connection, string reason, string? cause) =>
        new(connection, reason, cause, localEngineFailure: true);
}

/// <summary>SP-0133: a connection's output so far, as monotonic totals from the engine's own statistics.</summary>
internal readonly record struct AudioOutputCounters(long DecodedBlocks, long PlayedBuffers, long LostBuffers);
