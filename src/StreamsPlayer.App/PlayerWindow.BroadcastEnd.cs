using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0250: how this window tells a broadcast that ended from a connection that was lost (<c>LIVE-BROADCAST</c> items T
/// and U). The rules live in Core's <see cref="BroadcastEndWatch"/>; this partial feeds it what the window already
/// observes - the stream stopping, a reconnect attempt's answer, the directory's removal - and applies the verdict: the
/// ended state, with an explicit Reconnect, and no further attempt of its own.
/// <para>
/// A window whose channel has no broadcast descriptor, and a playlist window, read no end: they keep the bounded recovery
/// they always had. Every member here runs on the UI thread.
/// </para>
/// </summary>
public partial class PlayerWindow
{
    private BroadcastEndWatch? _endWatch;
    private IDisposable? _endFollow;
    private bool _broadcastEnded;

    /// <summary>Called once, before the first open: starts reading the end of a broadcast and following its directory.</summary>
    private void BeginReadingBroadcastEnd()
    {
        if (_channel.FastMediaSorterBroadcast is null || _playlistItems is not null || _endWatch is not null)
        {
            return;
        }

        _endWatch = new BroadcastEndWatch();
        StartFollowingBroadcast();
    }

    private void StartFollowingBroadcast()
    {
        _endFollow?.Dispose();
        _endFollow = _endWatch is null ? null : ((App)Application.Current).BroadcastEnd.Follow(_channel, OnBroadcastRemoved);
    }

    // Nothing plays after a verdict, so there is nothing to follow until the person reconnects.
    private void StopFollowingBroadcast()
    {
        _endFollow?.Dispose();
        _endFollow = null;
    }

    private void OnBroadcastRemoved()
    {
        if (_closing || _failureShown || _endWatch is not { } watch)
        {
            return;
        }

        EndBroadcast(watch.NoteRemoval());
    }

    /// <summary>The live stream stopped or failed to deliver. True when that ended the broadcast (a removal had been waiting).</summary>
    private bool NoteBroadcastStopped() => _endWatch is { } watch && EndBroadcast(watch.NoteStopped());

    /// <summary>A reconnect attempt was refused with <paramref name="reason"/>. True when that answer ended the broadcast.</summary>
    private bool NoteBroadcastAnswer(string reason) =>
        _endWatch is { } watch && EndBroadcast(watch.NoteAnswer(BroadcastEndAnswers.FromReason(reason)));

    private void NoteBroadcastLive() => _endWatch?.NoteLive();

    /// <summary>
    /// What a producer's LAN address answers now (<see cref="BroadcastLanProbe"/>), asked only for a broadcast this window has
    /// played and only for an address that is not the exchange server's own: a relay and a tunnel answer through the attempt
    /// itself. Runs on the open worker; anything that says nothing is <see cref="BroadcastEndAnswer.None"/>.
    /// </summary>
    private async Task<BroadcastEndAnswer> ProbeLanAsync(FastMediaSorterBroadcastEndpoint endpoint)
    {
        if (_endWatch is not { PlayedBefore: true } || ExchangeEndpointKinds.IsExchangeEndpoint(endpoint)
            || !LaunchableAddress.TryParse(endpoint.Url, out var address))
        {
            return BroadcastEndAnswer.None;
        }

        try
        {
            return await BroadcastLanProbe.ProbeAsync(address, _sessionCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return BroadcastEndAnswer.None; // the window closed while it asked
        }
    }

    /// <summary>
    /// SP-0250 requirement 6: the broadcast ended. The window stays open, says so, offers Reconnect, and attempts nothing:
    /// the same latch every recovery path already yields to (<c>_failureShown</c>) holds it, and the engine, the attempt's
    /// transport and the keep-awake hold are given back as for any final verdict. It is no failure of the channel, so no
    /// failed outcome is recorded against it.
    /// </summary>
    private bool EndBroadcast(BroadcastEndVerdict verdict)
    {
        if (!verdict.IsEnd)
        {
            return false;
        }

        if (_closing || _broadcastEnded || _failureShown)
        {
            return true;
        }

        _broadcastEnded = true;
        _failureShown = true;
        _recovering = false;
        StopFollowingBroadcast();
        ReleaseAfterFailure();
        NotifyPictureLive(); // the panel below says it; a caption that calls the channel unavailable would contradict it
        SetWaitTextResource(IncomingBroadcastFailure.EndedKey);
        ClearNowPlaying();
        _health.NotifyFailed(HealthNow);
        ApplySignalHealth();
        _sessionOutcome = "ended";
        _log.Event("PLAYBACK END", $"cause={BroadcastEndCauses.Name(verdict.Cause)}", "kind=video",
            $"at_ms={_playbackClock.ElapsedMilliseconds}", $"leg={_legCount}");
        ShowBroadcastEnded();
        return true;
    }

    private void ShowBroadcastEnded()
    {
        _broadcastFailureReason = null;
        BroadcastFailureText.Text = LocalizationService.Get(IncomingBroadcastFailure.EndedKey);
        BroadcastFailureDetails.Visibility = Visibility.Collapsed;
        BroadcastDiagnoseButton.Visibility = Visibility.Collapsed; // an ended broadcast is not a failure to diagnose
        BroadcastRetryButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "BroadcastReconnect");
        BroadcastFailurePanel.Visibility = Visibility.Visible;
    }

    /// <summary>The person's Reconnect on the ended state: listen again, follow the directory again, open as for a retry.</summary>
    private void ResumeAfterBroadcastEnd()
    {
        _broadcastEnded = false;
        _endWatch?.Reset();
        BroadcastRetryButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "FailureRetry");
        StartFollowingBroadcast();
    }
}
