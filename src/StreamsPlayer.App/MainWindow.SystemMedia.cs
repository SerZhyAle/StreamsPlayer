using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // SP-0021 opt-in Windows media integration. All fields are inert while the setting is off
    // (the default), so the app behaves exactly as before.
    private SystemMediaControls? _systemMediaControls;

    // The id a system Pause left behind. The row is always resolved from current state before it is
    // displayed or restarted, so a pause cannot later write or show a stale channel snapshot.
    private Guid? _audioPausedChannelId;

    private StreamChannel? PausedAudioChannel => _audioPausedChannelId is { } id ? ChannelById(id) : null;

    // The ordered audio launch context captured when playback started: the filtered view's
    // stable order at that moment. Previous/Next moves through this list, not the live view.
    private List<Guid> _audioNavOrder = [];

    // SP-0132: where each captured id sits in the order above, first occurrence winning as IndexOf did.
    // Built with the order, so finding the current station is a lookup rather than a scan of it.
    private Dictionary<Guid, int> _audioNavPositions = [];

    // SP-0132: the catalog by id, rebuilt only when _state.Channels is a different list. The list is always
    // replaced, never edited in place, so its identity is a sound cache key - the reasoning
    // BuildFaviconAtlasSet keys on. The compact panel asks on every status, now-playing and volume change;
    // scanning the catalog for each captured entry there was ~10^8 comparisons with the full bank, on the
    // UI thread, per volume tick.
    private Dictionary<Guid, StreamChannel>? _channelsById;
    private List<StreamChannel>? _channelsByIdSource;

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
            _systemMediaControls.Failed += OnSystemMediaFailed;
        }
    }

    // SP-0119: the instance stays, inert, so nothing re-creates it - the integration is off for this session.
    private void OnSystemMediaFailed(Exception exception)
    {
        _log.Event("SMTC OFF", $"type={exception.GetType().Name}", $"hresult=0x{exception.HResult:X8}");
        _log.Error("Windows media controls stopped responding; switched off for this session", exception);
    }

    private void DisposeSystemMediaControls()
    {
        if (_systemMediaControls is null)
        {
            return;
        }

        _systemMediaControls.CommandRequested -= OnSystemMediaCommand;
        _systemMediaControls.Failed -= OnSystemMediaFailed;
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
            else if (_audioPausedChannelId is not null)
            {
                PublishAudioSession(playing: false);
            }
        }
        else
        {
            _audioPausedChannelId = null;
            DisposeSystemMediaControls();
        }
    }

    private void CaptureAudioNavOrder()
    {
        _audioNavOrder = PinnedRows.Concat(Rows)
            .Where(row => row.Channel.MediaKind == MediaKind.Audio)
            .Select(row => row.Channel.Id)
            .ToList();
        _audioNavPositions = new Dictionary<Guid, int>(_audioNavOrder.Count);
        for (var index = 0; index < _audioNavOrder.Count; index++)
        {
            _audioNavPositions.TryAdd(_audioNavOrder[index], index);
        }
    }

    private StreamChannel? ChannelById(Guid id)
    {
        if (_channelsById is null || !ReferenceEquals(_channelsByIdSource, _state.Channels))
        {
            var channels = _state.Channels;
            var index = new Dictionary<Guid, StreamChannel>(channels.Count);
            foreach (var channel in channels)
            {
                // First occurrence wins, matching the FirstOrDefault this replaces.
                index.TryAdd(channel.Id, channel);
            }

            _channelsById = index;
            _channelsByIdSource = channels;
        }

        return _channelsById.GetValueOrDefault(id);
    }

    private int CurrentAudioNavIndex() =>
        (_playingAudio?.Channel.Id ?? _audioPausedChannelId) is Guid id &&
        _audioNavPositions.TryGetValue(id, out var index)
            ? index
            : -1;

    // An entry stops being available when its row has since been deleted from the catalog.
    private bool IsAudioNavEntryAvailable(int index) => ChannelById(_audioNavOrder[index]) is not null;

    private void PublishAudioSession(bool playing)
    {
        if (_systemMediaControls is null)
        {
            return;
        }

        var channel = playing ? _playingAudio?.Channel : PausedAudioChannel;
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

        var current = CurrentAudioNavIndex();
        return (LivePlaybackNavigation.PreviousAvailable(current, IsAudioNavEntryAvailable) is not null,
            LivePlaybackNavigation.NextAvailable(current, _audioNavOrder.Count, IsAudioNavEntryAvailable) is not null);
    }

    private void OnSystemMediaCommand(SystemMediaControls.Command command)
    {
        switch (command)
        {
            case SystemMediaControls.Command.Play:
                if (_playingAudio is null)
                {
                    ResumeAudio();
                }

                break;
            case SystemMediaControls.Command.Pause:
                if (_playingAudio is not null)
                {
                    PauseAudio();
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

    /// <summary>
    /// Stops the sound and keeps the station. Reached from the system flyout's Pause and, since SP-0081,
    /// from the panel's own transport button - the two are deliberately the same act, so the panel and
    /// the flyout can never disagree about what the session is.
    /// </summary>
    private void PauseAudio()
    {
        var channel = _playingAudio?.Channel;
        if (channel is null)
        {
            return;
        }

        // Live has no paused position, so pausing stops the session; the saved channel lets a later
        // Play restart it at the live edge. Keep the system session visible as Paused.
        StopAudioPlayback(clearSystemSession: false);
        _audioPausedChannelId = channel.Id;
        // SP-0081: after the field above - this is what turns the panel's controls back on and flips the
        // button to Resume, for a session that is silent but not finished.
        ApplyAudioTransportState();
        SetNowPlaying("PausedAudio", StreamTitleFormatter.Display(channel.Title));
        PublishAudioSession(playing: false);
        _ = StartPreviewsAsync();
    }

    private void ResumeAudio()
    {
        var channel = PausedAudioChannel;
        if (channel is null)
        {
            return;
        }

        _audioPausedChannelId = null;
        _ = PlayChannelAsync(channel, rememberSelection: true);
    }

    private void NavigateAudio(bool forward)
    {
        if (_audioNavOrder.Count == 0)
        {
            return;
        }

        var current = CurrentAudioNavIndex();
        var target = forward
            ? LivePlaybackNavigation.NextAvailable(current, _audioNavOrder.Count, IsAudioNavEntryAvailable)
            : LivePlaybackNavigation.PreviousAvailable(current, IsAudioNavEntryAvailable);
        if (target is not int index)
        {
            return; // no wrap: stop cleanly at either end
        }

        var channel = ChannelById(_audioNavOrder[index]);
        if (channel is null)
        {
            return;
        }

        _ = PlayChannelAsync(channel, rememberSelection: true);
    }
}
