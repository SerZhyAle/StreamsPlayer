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
    private bool _failed;

    internal event Action<Command>? CommandRequested;

    /// <summary>Raised once, on the calling thread, when the media session stops accepting calls.</summary>
    internal event Action<Exception>? Failed;

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

    internal void Publish(string station, string? track, bool playing, bool canPrevious, bool canNext) =>
        Drive(() =>
        {
            _controls.IsEnabled = true;
            _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            _controls.IsNextEnabled = canNext;
            _controls.IsPreviousEnabled = canPrevious;
            UpdateDisplay(station, track);
        });

    internal void UpdateMetadata(string station, string? track) => Drive(() => UpdateDisplay(station, track));

    /// <summary>Ends the published session and clears its metadata from the flyout.</summary>
    internal void Clear() =>
        Drive(() =>
        {
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.IsNextEnabled = false;
            _controls.IsPreviousEnabled = false;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
        });

    /// <summary>
    /// Runs one call into the media session, and switches the integration off for the rest of the session
    /// if the call fails (SP-0119).
    /// </summary>
    /// <remarks>
    /// These calls cross into a Windows service. A restarted or unavailable media-session service answers
    /// with a COM failure, and that used to reach the dispatcher and end the process over what is an
    /// optional convenience. The first failure is reported once through <see cref="Failed"/>; every later
    /// call is a no-op, the same as after <see cref="Dispose"/>.
    /// </remarks>
    private void Drive(Action call)
    {
        if (_disposed || _failed)
        {
            return;
        }

        try
        {
            call();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException
            or ObjectDisposedException or UnauthorizedAccessException)
        {
            _failed = true;
            Failed?.Invoke(exception);
        }
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
        try
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.IsEnabled = false;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException
            or ObjectDisposedException or UnauthorizedAccessException)
        {
            // The session is already tearing down, or its service is gone (SP-0119); nothing left to clear.
        }

        try
        {
            _mediaPlayer.Dispose();
        }
        catch (COMException)
        {
            // Same: releasing a player whose service restarted must not fail the caller's teardown.
        }
    }
}
