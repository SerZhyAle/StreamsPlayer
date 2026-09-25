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

    private readonly PlaybackConnectionSequence _connections = new();
    private Leg? _leg;
    private Media? _media;
    private bool _disposed;

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
        var connection = _connections.Open();

        try
        {
            var libVlc = SharedLibVlc.Value;
            _leg = new Leg(this, connection, new MediaPlayer(libVlc) { Volume = volume });
            _media = new Media(libVlc, uri, ":clock-jitter=0");
            if (!_leg.Player.Play(_media))
            {
                ReleaseLeg();
                Failed?.Invoke(this, new StandardAudioFailedEventArgs(connection, new InvalidOperationException("LibVLC rejected audio playback.")));
            }
        }
        catch (Exception ex)
        {
            // Released but still current: the owner handles this failure after it returns, and a failure nobody
            // may act on would leave the station connecting for ever. Its StopPlayback is what ends the identity.
            ReleaseLeg();
            Failed?.Invoke(this, new StandardAudioFailedEventArgs(connection, ex));
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

    /// <summary>Stops the connection; an event it raised that has not been handled yet is no longer current.</summary>
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
    }

    private void ReleaseLeg()
    {
        var leg = _leg;
        _leg = null;
        leg?.Release();
        _media?.Dispose();
        _media = null;
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
            _error = (_, _) => owner.Failed?.Invoke(owner, new StandardAudioFailedEventArgs(
                connection, new InvalidOperationException("LibVLC encountered error during audio playback.")));
            player.Playing += _playing;
            player.EndReached += _ended;
            player.EncounteredError += _error;
        }

        public MediaPlayer Player { get; }

        public void Release()
        {
            Player.Playing -= _playing;
            Player.EndReached -= _ended;
            Player.EncounteredError -= _error;
            Player.Stop();
            Player.Dispose();
        }
    }
}

internal class StandardAudioEventArgs(PlaybackConnectionId connection) : EventArgs
{
    /// <summary>The connection that raised the event (SP-0120); ask <see cref="StandardAudioPlayback.IsCurrent"/>.</summary>
    public PlaybackConnectionId Connection { get; } = connection;
}

internal sealed class StandardAudioFailedEventArgs(PlaybackConnectionId connection, Exception exception)
    : StandardAudioEventArgs(connection)
{
    public Exception Exception { get; } = exception;
}

/// <summary>SP-0133: a connection's output so far, as monotonic totals from the engine's own statistics.</summary>
internal readonly record struct AudioOutputCounters(long DecodedBlocks, long PlayedBuffers, long LostBuffers);
