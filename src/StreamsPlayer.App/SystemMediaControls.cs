using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Playback;

namespace StreamsPlayer.App;

/// <summary>
/// Thin wrapper over the WinRT System Media Transport Controls for the single inline audio
/// session (SP-0021). All Windows Runtime usage is contained here so the rest of the App
/// stays on plain CLR types. A source-less <see cref="MediaPlayer"/> supplies a real SMTC
/// instance in a Win32/WPF process; its own command manager is disabled so this class drives
/// state manually. Button presses arrive on a WinRT pool thread and are marshalled back to the
/// captured UI <see cref="SynchronizationContext"/> before <see cref="CommandRequested"/> fires.
/// </summary>
internal sealed class SystemMediaControls : IDisposable
{
    internal enum Command
    {
        Play,
        Pause,
        Stop,
        Next,
        Previous
    }

    private readonly MediaPlayer _mediaPlayer;
    private readonly SystemMediaTransportControls _controls;
    private readonly SynchronizationContext? _uiContext;
    private bool _disposed;

    internal event Action<Command>? CommandRequested;

    private SystemMediaControls(MediaPlayer mediaPlayer, SynchronizationContext? uiContext)
    {
        _mediaPlayer = mediaPlayer;
        _uiContext = uiContext;
        _controls = mediaPlayer.SystemMediaTransportControls;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsStopEnabled = true;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
    }

    /// <summary>
    /// Creates the controls, or returns <c>null</c> if the Windows Runtime projection or the
    /// media session cannot be reached (older/headless Windows). The caller treats <c>null</c>
    /// as "feature unavailable" and continues with the ordinary in-app behaviour.
    /// </summary>
    internal static SystemMediaControls? TryCreate()
    {
        try
        {
            var player = new MediaPlayer();
            // Drive the transport controls by hand instead of from a (non-existent) media source.
            player.CommandManager.IsEnabled = false;
            return new SystemMediaControls(player, SynchronizationContext.Current);
        }
        catch (Exception exception) when (exception is TypeLoadException or PlatformNotSupportedException
            or COMException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    internal void Publish(string station, string? track, bool playing, bool canPrevious, bool canNext)
    {
        if (_disposed)
        {
            return;
        }

        _controls.IsEnabled = true;
        _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        _controls.IsNextEnabled = canNext;
        _controls.IsPreviousEnabled = canPrevious;
        UpdateDisplay(station, track);
    }

    internal void UpdateMetadata(string station, string? track)
    {
        if (_disposed)
        {
            return;
        }

        UpdateDisplay(station, track);
    }

    /// <summary>Ends the published session and clears its metadata from the flyout.</summary>
    internal void Clear()
    {
        if (_disposed)
        {
            return;
        }

        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.IsNextEnabled = false;
        _controls.IsPreviousEnabled = false;
        _controls.DisplayUpdater.ClearAll();
        _controls.DisplayUpdater.Update();
    }

    private void UpdateDisplay(string station, string? track)
    {
        var updater = _controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        var hasTrack = !string.IsNullOrWhiteSpace(track);
        updater.MusicProperties.Title = hasTrack ? track : station;
        updater.MusicProperties.Artist = hasTrack ? station : string.Empty;
        updater.Update();
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        Command? command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => Command.Play,
            SystemMediaTransportControlsButton.Pause => Command.Pause,
            SystemMediaTransportControlsButton.Stop => Command.Stop,
            SystemMediaTransportControlsButton.Next => Command.Next,
            SystemMediaTransportControlsButton.Previous => Command.Previous,
            _ => null
        };
        if (command is Command resolved)
        {
            Raise(resolved);
        }
    }

    private void Raise(Command command)
    {
        var handler = CommandRequested;
        if (handler is null)
        {
            return;
        }

        if (_uiContext is not null)
        {
            _uiContext.Post(_ => handler(command), null);
        }
        else
        {
            handler(command);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.IsEnabled = false;
        try
        {
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
        }
        catch (COMException)
        {
            // The session is already tearing down; nothing left to clear.
        }

        _mediaPlayer.Dispose();
    }
}
