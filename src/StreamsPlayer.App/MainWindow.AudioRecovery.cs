using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0120: the radio's bounded recovery and the filter in front of it - which events may reach it, and how
// many recoveries may run at once.
public partial class MainWindow
{
    // The session (_audioRecoveryCts) whose recovery is running, or null; see RecoverAudioAsync.
    private CancellationTokenSource? _audioRecoveryInFlightFor;

    /// <summary>
    /// SP-0120: an event raised by a radio connection that has since been stopped or replaced. The three radio
    /// handlers run on the UI thread some time after the engine raised them, and they act on whatever is playing
    /// when they run - so a failure queued by the previous connection used to stop its successor and spend the
    /// recovery budget a second time. Asked here, on the UI thread, where connections are opened and stopped.
    /// </summary>
    private bool IsSupersededAudioEvent(StandardAudioEventArgs e, string kind)
    {
        if (_standardAudioPlayback.IsCurrent(e.Connection))
        {
            return false;
        }

        _log.Event("AUDIO EVENT IGNORED", $"event={kind}", "why=superseded", $"connection={e.Connection.Value}");
        return true;
    }

    // Bounded audio recovery (DEVELOPER_PROMPT.md Part D). Classifies the failure, then reconnects after a cancellable
    // backoff (showing a Reconnecting label) or, once the budget is spent or a hard failure is hit, shows the
    // terminal dialog. There is no position stall-watchdog for audio - it was ruled out while radio ran on
    // MediaElement, which exposed no live telemetry, and the LibVLC engine (SP-0104) has not been given one.
    private async Task RecoverAudioAsync(
        StreamChannel channel,
        string reason,
        bool endReached = false,
        bool openTimedOut = false,
        int? firstResponseStatusCode = null,
        bool hasFirstResponseStatus = false,
        FastMediaSorterPlaybackFailureKind? fastMediaSorterFailure = null)
    {
        var policy = _audioRecovery;
        var cts = _audioRecoveryCts;
        if (policy is null || cts is null || cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return; // audio was stopped or switched to another channel
        }

        // SP-0120: one recovery per session at a time. A second failure signal for the same interruption - the
        // open budget expiring during a failure's probe or backoff, an error and an end queued together - used to
        // start a second decision, spending the budget twice and opening the station twice. Keyed on the
        // session's cancellation source, so a Retry that starts a new session is never held back by the old one.
        if (ReferenceEquals(_audioRecoveryInFlightFor, cts))
        {
            _log.Event("AUDIO RECOVER IGNORED", "why=in_flight", $"reason={reason}", $"url={channel.Url}");
            return;
        }

        _audioRecoveryInFlightFor = cts;
        try
        {
            await RecoverAudioInFlightAsync(channel, reason, policy, cts, endReached, openTimedOut,
                firstResponseStatusCode, hasFirstResponseStatus, fastMediaSorterFailure);
        }
        finally
        {
            if (ReferenceEquals(_audioRecoveryInFlightFor, cts))
            {
                _audioRecoveryInFlightFor = null;
            }
        }
    }

    private async Task RecoverAudioInFlightAsync(
        StreamChannel channel,
        string reason,
        LivePlaybackRecoveryPolicy policy,
        CancellationTokenSource cts,
        bool endReached,
        bool openTimedOut,
        int? firstResponseStatusCode,
        bool hasFirstResponseStatus,
        FastMediaSorterPlaybackFailureKind? fastMediaSorterFailure)
    {
        // Only a fresh open failure needs the status probe; a stream that ended already carries its own
        // signal, and probing it would spend a request to learn nothing. Same rule the video path applies.
        // SP-0096 is the second such case: the source has just had the full open budget to answer, so
        // asking it again buys nothing but more of the wait that budget exists to end.
        // SP-0041: the same fresh-open condition selects the connectivity gate, in the same order as the
        // video path - an already-playing stream (end, open verdict, a status already in hand) is not gated.
        var reachability = PlaybackReachability.NotProbed;
        int? status = hasFirstResponseStatus ? firstResponseStatusCode : null;
        if (!hasFirstResponseStatus && !endReached && !openTimedOut)
        {
            reachability = await StreamReachabilityProbe.ProbeAsync(channel.Url, cts.Token);
            if (cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
            {
                return; // stopped or switched while probing - no dialog, no restart
            }

            _log.Event("AUDIO REACH", $"verdict={reachability}", $"url={channel.Url}");
            // A host that refused the connection cannot answer a status request either.
            status = PlaybackReachabilityRules.SpendsRecoveryBudget(reachability)
                ? await PlaybackStatusProbe.TryGetStatusAsync(channel.Url, cts.Token)
                : null;
        }

        if (cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return; // stopped or switched while probing - do not relabel or restart
        }

        if (!PlaybackReachabilityRules.SpendsRecoveryBudget(reachability))
        {
            // Decisions 3 and 4: the policy is never consulted, so no attempt is spent.
            await FailAudioTerminallyAsync(channel, reason, fastMediaSorterFailure, reachability);
            return;
        }

        var decision = policy.Decide(new PlaybackFailureSignal(reason, EndReached: endReached, HttpStatusCode: status, OpenTimedOut: openTimedOut));
        _log.Event("AUDIO RECOVER",
            $"trigger={decision.Trigger}",
            $"action={decision.Kind}",
            $"attempt={decision.Attempt}",
            $"budget={decision.Budget}",
            $"delay_ms={decision.Delay.TotalMilliseconds:F0}",
            $"reason={reason}",
            $"http={status?.ToString() ?? "n/a"}",
            $"url={channel.Url}");

        if (decision.Kind == RecoveryActionKind.HardFail)
        {
            await FailAudioTerminallyAsync(channel, reason, fastMediaSorterFailure);
            return;
        }

        SetNowPlaying("ReconnectingAudioAttempt", StreamTitleFormatter.Display(channel.Title), decision.Attempt, decision.Budget);
        try
        {
            await Task.Delay(decision.Delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // stop / switch / close cancelled the wait - never restart the old station
        }

        if (cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return;
        }

        StartAudioPlayback(channel, reconnecting: true);
    }
}
