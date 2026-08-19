using System.Net.NetworkInformation;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0086: "play a random radio station" - one press, one independent draw from the whole catalog, and
/// a silent walk to the next draw for as long as the drawn stations refuse to speak.
/// </summary>
/// <remarks>
/// The hunt is a short-lived session rather than a flag, because it owns two things the ordinary playback
/// path does not have and must not gain: a deadline for a station that has never produced a sound
/// (<c>MediaElement</c> raises neither MediaOpened nor MediaFailed for one that connects and then stays
/// silent), and a refusal that costs nothing - no status probe, no reconnect budget, no modal. The moment
/// a station reaches MediaOpened the session ends and the station becomes an ordinary station, carrying
/// the recovery policy <see cref="MainWindow.PlayChannelAsync"/> already installed for it. That single
/// hand-off is why an ordinary click keeps its dialog and its reconnects with no second code path.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// One press. Every <c>await</c> in the loop resumes against this object's identity rather than a
    /// boolean, so a hunt that was superseded or cancelled mid-await can never write to the UI.
    /// </summary>
    private sealed class RandomStationHunt
    {
        public CancellationTokenSource Cts { get; } = new();
        public Guid ProbeChannelId { get; set; }
        public TaskCompletionSource<bool> Outcome { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private RandomStationHunt? _randomHunt;

    // One generator for the window's lifetime, touched from the UI thread only. A fresh Random per press
    // is the classic way to get a correlated sequence out of a clock-seeded generator.
    private readonly Random _randomStationRoll = new();

    private async void PlayRandomStationMenuItem_Click(object sender, RoutedEventArgs e) =>
        await StartRandomStationHuntAsync();

    private async Task StartRandomStationHuntAsync()
    {
        CancelRandomStationHunt(); // a second press restarts the hunt; it never runs two

        // Built once per press, not once per attempt: this walks the whole catalog, and the owner's is
        // about 20 000 rows.
        var eligible = RandomStationSelection.Eligible(_state.Channels, _state.HiddenCatalogUrls);
        if (eligible.Count == 0)
        {
            SetStatus("RandomStationNone");
            return;
        }

        var policy = RandomStationHuntPolicy.Default;
        var session = new RandomStationHunt();
        _randomHunt = session;
        _log.Event("RANDOM START", $"eligible={eligible.Count}", $"attempts={policy.MaxAttempts}");

        try
        {
            for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
            {
                // Checked per attempt rather than once: PlayChannelAsync answers an offline machine with a
                // MessageBox, and a hunt that lost the network mid-walk would stack one per remaining
                // attempt. This is also the only exit that is not the user's doing.
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    SetStatus("OfflinePlayback");
                    return;
                }

                if (RandomStationSelection.Draw(eligible, _randomStationRoll) is not { } channel)
                {
                    return;
                }

                SetStatus("RandomStationTrying", StreamTitleFormatter.Display(channel.Title), attempt, policy.MaxAttempts);
                if (await TryRandomStationAsync(session, channel, policy))
                {
                    return; // live: the ordinary now-playing line is already on screen
                }

                if (!ReferenceEquals(_randomHunt, session))
                {
                    return; // superseded by a user action or cancelled while we were waiting
                }
            }

            StopAudioPlayback();
            SetStatus("RandomStationExhausted", policy.MaxAttempts);
            _log.Event("RANDOM EXHAUSTED", $"attempts={policy.MaxAttempts}");
        }
        finally
        {
            if (ReferenceEquals(_randomHunt, session))
            {
                _randomHunt = null;
                session.Cts.Dispose();
            }
        }
    }

    private async Task<bool> TryRandomStationAsync(RandomStationHunt session, StreamChannel channel, RandomStationHuntPolicy policy)
    {
        session.ProbeChannelId = channel.Id;
        session.Outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // rememberSelection: false is what leaves the list alone - it is the only thing on this path that
        // would write the selection, and nothing here reveals, filters or scrolls.
        await PlayChannelAsync(channel, rememberSelection: false, randomHunt: true);
        if (!ReferenceEquals(_randomHunt, session))
        {
            return false;
        }

        var outcome = session.Outcome.Task;
        Task deadline;
        try
        {
            deadline = Task.Delay(policy.ConnectTimeout, session.Cts.Token);
        }
        catch (ObjectDisposedException)
        {
            return false; // the session was cancelled and disposed between the two checks above
        }

        var settled = await Task.WhenAny(outcome, deadline);
        if (!ReferenceEquals(_randomHunt, session))
        {
            return false;
        }

        if (settled == outcome && outcome.Result)
        {
            _randomHunt = null; // the station is ordinary from here; the hunt is over
            session.Cts.Cancel(); // release the pending Task.Delay rather than leaving it to time out
            session.Cts.Dispose();
            return true;
        }

        // A refusal, an end-of-stream, or the deadline. A refusal already logged itself in
        // YieldToRandomStationHunt; only the deadline is news here - it is the one outcome no existing
        // handler can report, because nothing was raised at all.
        if (settled != outcome)
        {
            _log.Event("RANDOM SKIP", "reason=timeout", $"url={channel.Url}");
        }

        // StopAudioPlayback and not StopAudio: the sleep timer's deadline belongs to the user, and a
        // station that never played must not cancel it.
        StopAudioPlayback();
        return false;
    }

    /// <summary>
    /// The failure intercept, called from the MediaFailed and MediaEnded handlers before they reach
    /// <c>RecoverAudioAsync</c>. Returns <c>true</c> when the hunt has taken the failure over.
    /// </summary>
    /// <remarks>
    /// Short-circuiting here rather than suppressing the dialog at the end of the recovery walk is the
    /// point: it skips the HTTP status probe, both backoff waits and the modal in one branch, which is the
    /// difference between a hunt and a thirty-second stall per dead station. It also leaves
    /// <c>RecoverAudioAsync</c>, <see cref="LivePlaybackRecoveryPolicy"/> and <see cref="PlaybackFailureDialog"/>
    /// untouched. Scoped by the probe id, so a station that already went live - and is therefore no
    /// longer any hunt's probe - takes the ordinary recovery path.
    /// </remarks>
    private bool YieldToRandomStationHunt(StreamChannel channel, string reason)
    {
        if (_randomHunt is not { } hunt || hunt.ProbeChannelId != channel.Id)
        {
            return false;
        }

        _log.Event("RANDOM SKIP", $"reason={reason}", $"url={channel.Url}");
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        hunt.Outcome.TrySetResult(false);
        return true;
    }

    /// <summary>Ends any hunt in flight. Idempotent, and safe to call from a hunt-free window.</summary>
    private void CancelRandomStationHunt()
    {
        if (_randomHunt is not { } session)
        {
            return;
        }

        _randomHunt = null;
        session.Outcome.TrySetResult(false); // never strand an awaiter
        session.Cts.Cancel();
        session.Cts.Dispose();
    }
}
