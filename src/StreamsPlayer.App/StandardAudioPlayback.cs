using System;
using LibVLCSharp.Shared;

namespace StreamsPlayer.App;

/// <summary>
/// LibVLC-based audio playback engine for standard radio streams.
/// Replaces WPF <see cref="System.Windows.Controls.MediaElement"/> to avoid CAS/pack URI restrictions
/// on HTTP/HTTPS audio streams in modern .NET environments.
/// </summary>
internal sealed class StandardAudioPlayback : IDisposable
{
    private static readonly Lazy<LibVLC> SharedLibVlc = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-video", "--no-video-title-show", "--no-osd", "--quiet", "--clock-jitter=0");
    });

    private MediaPlayer? _player;
    private Media? _media;
    private bool _disposed;

    public event EventHandler? Playing;
    public event EventHandler? Ended;
    public event EventHandler<StandardAudioFailedEventArgs>? Failed;

    public bool IsPlaying => _player?.IsPlaying ?? false;

    public void Play(Uri uri, int volume)
    {
        ThrowIfDisposed();
        StopPlayback();

        try
        {
            var libVlc = SharedLibVlc.Value;
            _player = new MediaPlayer(libVlc) { Volume = volume };
            _player.Playing += Player_Playing;
            _player.EndReached += Player_EndReached;
            _player.EncounteredError += Player_EncounteredError;

            _media = new Media(libVlc, uri, ":clock-jitter=0");
            if (!_player.Play(_media))
            {
                StopPlayback();
                Failed?.Invoke(this, new StandardAudioFailedEventArgs(new InvalidOperationException("LibVLC rejected audio playback.")));
            }
        }
        catch (Exception ex)
        {
            StopPlayback();
            Failed?.Invoke(this, new StandardAudioFailedEventArgs(ex));
        }
    }

    public void SetVolume(int volume)
    {
        if (_player is not null)
        {
            _player.Volume = volume;
        }
    }

    public void Pause()
    {
        if (_player is not null && _player.IsPlaying)
        {
            _player.Pause();
        }
    }

    public void Resume()
    {
        if (_player is not null && !_player.IsPlaying)
        {
            _player.Play();
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
            player.Stop();
            player.Dispose();
        }

        _media?.Dispose();
        _media = null;
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

    private void Player_Playing(object? sender, EventArgs e) => Playing?.Invoke(this, EventArgs.Empty);

    private void Player_EndReached(object? sender, EventArgs e) => Ended?.Invoke(this, EventArgs.Empty);

    private void Player_EncounteredError(object? sender, EventArgs e) =>
        Failed?.Invoke(this, new StandardAudioFailedEventArgs(new InvalidOperationException("LibVLC encountered error during audio playback.")));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class StandardAudioFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
