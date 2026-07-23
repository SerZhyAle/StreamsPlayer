using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // SP-0021 opt-in Windows media integration. All fields are inert while the setting is off
    // (the default), so the app behaves exactly as before.
    private SystemMediaControls? _systemMediaControls;

    // The saved channel a system Pause left behind, so a later system Play restarts it at the
    // live edge. Non-null only while paused; cleared on any real stop or channel switch.
    private StreamChannel? _audioPausedChannel;

    // The ordered audio launch context captured when playback started: the filtered view's
    // stable order at that moment. Previous/Next moves through this list, not the live view.
    private List<Guid> _audioNavOrder = [];

    // Latest ICY track text for the playing channel, mirrored into the system session title.
    private string? _currentTrackText;

    private void EnsureSystemMediaControls()
    {
        if (!_state.SystemMediaControls || _systemMediaControls is not null)
        {
            return;
        }

        _systemMediaControls = SystemMediaControls.TryCreate();
        if (_systemMediaControls is not null)
        {
            _systemMediaControls.CommandRequested += OnSystemMediaCommand;
        }
    }

    private void DisposeSystemMediaControls()
    {
        if (_systemMediaControls is null)
        {
            return;
        }

        _systemMediaControls.CommandRequested -= OnSystemMediaCommand;
        _systemMediaControls.Dispose();
        _systemMediaControls = null;
    }

    // Reflects a live Settings toggle. Enabling publishes any current/paused session; disabling
    // tears the integration down and forgets a paused session (there is no UI to resume it).
    private void ApplySystemMediaControlsSetting()
    {
        if (_state.SystemMediaControls)
        {
            EnsureSystemMediaControls();
            if (_playingAudio is not null)
            {
                PublishAudioSession(playing: true);
            }
            else if (_audioPausedChannel is not null)
            {
                PublishAudioSession(playing: false);
            }
        }
        else
        {
            _audioPausedChannel = null;
            DisposeSystemMediaControls();
        }
    }

    private List<Guid> CaptureAudioNavOrder() =>
        PinnedRows.Concat(Rows)
            .Where(row => row.Channel.MediaKind == MediaKind.Audio)
            .Select(row => row.Channel.Id)
            .ToList();

    private void PublishAudioSession(bool playing)
    {
        if (_systemMediaControls is null)
        {
            return;
        }

        var channel = playing ? _playingAudio?.Channel : _audioPausedChannel;
        if (channel is null)
        {
            return;
        }

        var (canPrevious, canNext) = AudioNavAvailability();
        _systemMediaControls.Publish(
            StreamTitleFormatter.Display(channel.Title), _currentTrackText, playing, canPrevious, canNext);
    }

    private void UpdateSystemMediaMetadata(string? track)
    {
        _currentTrackText = track;
        if (_systemMediaControls is null || _playingAudio is null)
        {
            return;
        }

        _systemMediaControls.UpdateMetadata(StreamTitleFormatter.Display(_playingAudio.Channel.Title), track);
    }

    private void ClearSystemMediaSession() => _systemMediaControls?.Clear();

    private (bool CanPrevious, bool CanNext) AudioNavAvailability()
    {
        if (_audioNavOrder.Count == 0)
        {
            return (false, false);
        }

        var currentId = _playingAudio?.Channel.Id ?? _audioPausedChannel?.Id;
        var current = currentId is Guid id ? _audioNavOrder.IndexOf(id) : -1;
        var available = _audioNavOrder
            .Select(channelId => _state.Channels.Any(channel => channel.Id == channelId))
            .ToList();
        return (LivePlaybackNavigation.PreviousAvailable(current, available) is not null,
            LivePlaybackNavigation.NextAvailable(current, available) is not null);
    }

    private void OnSystemMediaCommand(SystemMediaControls.Command command)
    {
        switch (command)
        {
            case SystemMediaControls.Command.Play:
                if (_playingAudio is null)
                {
                    ResumeAudioFromSystemMedia();
                }

                break;
            case SystemMediaControls.Command.Pause:
                if (_playingAudio is not null)
                {
                    PauseAudioForSystemMedia();
                }

                break;
            case SystemMediaControls.Command.Stop:
                StopAudio();
                break;
            case SystemMediaControls.Command.Next:
                NavigateAudio(forward: true);
                break;
            case SystemMediaControls.Command.Previous:
                NavigateAudio(forward: false);
                break;
        }
    }

    private void PauseAudioForSystemMedia()
    {
        var channel = _playingAudio?.Channel;
        if (channel is null)
        {
            return;
        }

        // Live has no paused position, so pausing stops the session; the saved channel lets a later
        // Play restart it at the live edge. Keep the system session visible as Paused.
        StopAudioPlayback(clearSystemSession: false);
        _audioPausedChannel = channel;
        SetNowPlaying("PausedAudio", StreamTitleFormatter.Display(channel.Title));
        PublishAudioSession(playing: false);
        _ = StartPreviewsAsync();
    }

    private void ResumeAudioFromSystemMedia()
    {
        var channel = _audioPausedChannel;
        if (channel is null)
        {
            return;
        }

        _audioPausedChannel = null;
        _ = PlayChannelAsync(channel, rememberSelection: true);
    }

    private void NavigateAudio(bool forward)
    {
        if (_audioNavOrder.Count == 0)
        {
            return;
        }

        var currentId = _playingAudio?.Channel.Id ?? _audioPausedChannel?.Id;
        var current = currentId is Guid id ? _audioNavOrder.IndexOf(id) : -1;
        var available = _audioNavOrder
            .Select(channelId => _state.Channels.Any(channel => channel.Id == channelId))
            .ToList();
        var target = forward
            ? LivePlaybackNavigation.NextAvailable(current, available)
            : LivePlaybackNavigation.PreviousAvailable(current, available);
        if (target is not int index)
        {
            return; // no wrap: stop cleanly at either end
        }

        var nextId = _audioNavOrder[index];
        var channel = _state.Channels.FirstOrDefault(candidate => candidate.Id == nextId);
        if (channel is null)
        {
            return;
        }

        _ = PlayChannelAsync(channel, rememberSelection: true);
    }
}
