using System.ComponentModel;
using StreamsPlayer.Core;

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
    // so closing them is this handler's job. (It also used to keep the last player's close from tripping the
    // last-window shutdown; since SP-0120 the process ends only once the catalog's close work is done.)
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // SP-0120: first. Nothing cancels this close, and closing the players below runs their Closed callbacks,
        // which reach the preview funnel - with the latch still clear, the last one restarted capture.
        _shuttingDown = true;
        // SP-0086: before the freeze. A hunt's in-flight probe is not a station the next launch should
        // bring back, and a hunt that outlived its window would write a status line into a dead one.
        CancelRandomStationHunt();
        _resumeRecordFrozen = true;
        CloseOpenPlayerWindows();
        // SP-0080: the compact panel is a top-level window of this application's making, so it goes the
        // same way, while the catalog is still open.
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
            await UpdateResumeRecordAsync(_ => []);
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
        return UpdateResumeRecordAsync(state =>
            state.Channels.Any(channel => channel.Id == channelId)
                ? [.. state.ResumeChannelIds, channelId]
                : null);
    }

    private Task NoteStreamStoppedAsync(Guid channelId)
    {
        return UpdateResumeRecordAsync(state =>
        {
            var updated = new List<Guid>(state.ResumeChannelIds);
            // One occurrence, not all: two player windows on the same channel are two entries, and closing one
            // of them must not forget the other.
            return updated.Remove(channelId) ? updated : null;
        });
    }

    private async Task UpdateResumeRecordAsync(Func<CatalogState, List<Guid>?> update)
    {
        if (_resumeRecordFrozen)
        {
            return;
        }

        _state = await PersistAsync(state =>
        {
            if (!state.ResumePlaybackOnStartup || _resumeRecordFrozen)
            {
                return state;
            }

            var updated = update(state);
            return updated is null ? state : state with { ResumeChannelIds = updated };
        });
    }
}
