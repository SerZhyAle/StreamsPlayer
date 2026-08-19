using System.ComponentModel;

namespace StreamsPlayer.App;

// SP-0062: resume at startup whatever was playing when the application last closed, when the user has
// asked for it. Two independent pieces live here: the "quiet" flag that keeps a resumed stream from
// raising a modal window, and the record of what is currently playing.
public partial class MainWindow
{
    // Set for a stream started by the startup resume, cleared the first time that station reaches live.
    // Assigned on every audio start in PlayChannelAsync, so an ordinary user play resets it for free.
    private bool _audioQuiet;

    // Shutdown tears playback down through the very hooks that maintain the record: closing this window
    // closes the player windows it tracks, and their Closed handler removes them from the record. Without
    // this latch the act of quitting would empty the list that quitting is supposed to preserve.
    private bool _resumeRecordFrozen;

    // Freeze the record first, tear the players down second - that ordering is the whole reason this is
    // Closing and not Closed. The players are no longer owned windows (see OpenIndependentPlayerWindow),
    // so closing them is this handler's job; doing it while the catalog is still open also keeps the last
    // player's close from tripping the last-window shutdown before the catalog has saved its state.
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // SP-0086: before the freeze. A hunt's in-flight probe is not a station the next launch should
        // bring back, and a hunt that outlived its window would write a status line into a dead one.
        CancelRandomStationHunt();
        _resumeRecordFrozen = true;
        CloseOpenPlayerWindows();
        // SP-0080: the compact panel is a top-level window of this application's making, so it goes the
        // same way and for the same reason - the catalog must still be open when its last companion
        // closes, or OnLastWindowClose fires before the state below has been saved.
        CloseCompactPanel();
    }

    // Read the record and clear it in one move, at the start of every launch however it was launched.
    // Nothing is playing at this moment, so an uncleared record would be a lie: the live hooks below are
    // what rebuild it for this session. Without the clear, resuming channel A would append A on top of the
    // A already recorded and the list would grow by its own length on every launch.
    private async Task<IReadOnlyList<Guid>> TakeRecordedPlaybackAsync()
    {
        var recorded = _state.ResumeChannelIds.ToList();
        if (recorded.Count > 0)
        {
            await UpdateResumeRecordAsync([]);
        }

        return recorded;
    }

    // Reached only from the argument-free launch branch: an explicit --url or --id is matched earlier in
    // StartRequestedPlaybackAsync's switch, so argument precedence needs no check of its own here.
    private async Task ResumeRecordedPlaybackAsync(IReadOnlyList<Guid> recorded)
    {
        // Checked before anything is resolved, so a launch with the preference off touches no channel and
        // makes no network request on the playback path. A state load that failed leaves _state at its
        // default, so this also covers the case where nothing was read from disk at all.
        if (!_state.ResumePlaybackOnStartup || recorded.Count == 0)
        {
            return;
        }

        var resumed = 0;
        // Sequentially, in the order the streams started. Not a fan-out: the audio branch mutates shared
        // player state, and opening several video windows at once would multiply the connection burst.
        foreach (var channelId in recorded)
        {
            var channel = _state.Channels.FirstOrDefault(item => item.Id == channelId);
            if (channel is null)
            {
                // Deleted, hidden, or dropped by a refresh while the application was closed. Skipped in
                // silence and never re-created - a re-created row would be a Manual one, which the ticket
                // rules out. The other recorded streams are unaffected.
                _log.Event("REFUSE", "op=resume", "reason=missing", $"id={channelId}");
                continue;
            }

            await PlayChannelAsync(channel, rememberSelection: false, quiet: true);
            resumed++;
        }

        SetStatus(resumed > 0 ? "ResumedPlayback" : "ResumeNothingToPlay");
    }

    private Task NoteStreamStartedAsync(Guid channelId)
    {
        // An externally launched channel is minted with a fresh id on every launch and is never persisted,
        // so recording one would store an id that can never resolve again. Same membership test
        // RememberSelectedChannelAsync uses.
        if (!_state.Channels.Any(channel => channel.Id == channelId))
        {
            return Task.CompletedTask;
        }

        return UpdateResumeRecordAsync([.. _state.ResumeChannelIds, channelId]);
    }

    private Task NoteStreamStoppedAsync(Guid channelId)
    {
        var updated = new List<Guid>(_state.ResumeChannelIds);
        // One occurrence, not all: two player windows on the same channel are two entries, and closing one
        // of them must not forget the other.
        if (!updated.Remove(channelId))
        {
            return Task.CompletedTask;
        }

        return UpdateResumeRecordAsync(updated);
    }

    private Task UpdateResumeRecordAsync(List<Guid> updated)
    {
        if (_resumeRecordFrozen || !_state.ResumePlaybackOnStartup)
        {
            return Task.CompletedTask;
        }

        // The in-memory swap happens before the await, not after it. A station switch calls the stop hook
        // and then the start hook in the same turn, and if each read _state on its own side of an await the
        // later save would be built on a stale list and lose the earlier change.
        _state = _state with { ResumeChannelIds = updated };
        return PersistResumeRecordAsync();
    }

    private async Task PersistResumeRecordAsync() => _state = await PersistAsync(_state);
}
