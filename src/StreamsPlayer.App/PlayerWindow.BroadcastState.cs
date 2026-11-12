using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class PlayerWindow
{
    private string? _broadcastFailureReason;

    private void ShowBroadcastFailure(string reason)
    {
        _broadcastFailureReason = reason;
        StopFollowingBroadcast(); // a failed window is not playing; Retry follows again
        RefreshBroadcastFailure();
        BroadcastFailurePanel.Visibility = Visibility.Visible;
        if (ExchangeFailurePresentation.BroadcastRow(reason) is { } row)
            _log.Event("BROADCAST FAILURE", $"code={row.Code}");
    }

    private void RefreshBroadcastFailure()
    {
        if (_broadcastEnded)
        {
            ShowBroadcastEnded(); // the ended text is assigned, so a language switch replays it
            return;
        }

        if (_broadcastFailureReason is not { } reason) return;
        // The generic line: no refused password and no certificate that does not match, which already say what to do.
        var generic = ExchangeBroadcastCertificateException.MessageKey(reason) is null && ExchangeBroadcastAuth.MessageKey(reason) is null;
        var row = ExchangeFailurePresentation.BroadcastRow(reason)
            ?? (generic ? ExchangeFailureRows.Unavailable : null);
        BroadcastDiagnoseButton.Visibility = generic && _channel.FastMediaSorterBroadcast?.DiagnosisTarget(_channel.Url) is not null
            ? Visibility.Visible : Visibility.Collapsed;
        BroadcastFailureText.Text = row is not null ? ExchangeFailurePresentation.Text(row)
            : LocalizationService.Get(ExchangeBroadcastCertificateException.MessageKey(reason)
                ?? ExchangeBroadcastAuth.MessageKey(reason) ?? IncomingBroadcastFailure.NoLongerAvailableKey);
        BroadcastFailureDetails.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        BroadcastFailureCode.Text = row is null ? "" : LocalizationService.Format("ExchangeDiagnosisCodeLine", row.Code);
    }

    /// <summary>
    /// SP-0225 requirement 2: the connection check of a failed incoming video broadcast, opened by the person's click
    /// alone. The same request the audio window builds - the server's authority, never the path or the id behind it.
    /// </summary>
    private void BroadcastDiagnose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_channel.FastMediaSorterBroadcast?.DiagnosisTarget(_channel.Url) is { } target)
            {
                var request = new ExchangeDiagnosisRequest(ExchangeFailureRows.Unreachable,
                    IncomingBroadcastFailure.NoLongerAvailableKey, target.Host, target.Port, false,
                    StreamTitleFormatter.Display(_channel.Title));
                new ExchangeDiagnosisWindow(request) { Owner = this }.ShowDialog();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(PlayerWindow)}.{nameof(BroadcastDiagnose_Click)}", exception);
        }
    }

    private void BroadcastRetry_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(BroadcastRetry_Click), async () =>
    {
        if (_closing || !_failureShown)
        {
            return;
        }

        BroadcastFailurePanel.Visibility = Visibility.Collapsed;
        if (_broadcastEnded)
        {
            ResumeAfterBroadcastEnd();
        }
        else
        {
            StartFollowingBroadcast();
        }

        _recovery.Reset();
        _recovering = false;
        DropRememberedCeilingOnMiss();
        _broadcastCredentials?.ResolveForRetry(_channel.Id);
        // S10-2: connecting again, by hand - the tracker would otherwise stay on Unavailable for the whole retry (as in the dialog's Retry).
        NotifyInterrupted(PlaybackInterruptionKind.Connecting);
        await RetryAfterFailureAsync();
    });

    /// <summary>
    /// SP-0222: what the broadcast list shows for a video broadcast opened in this window - the only live state
    /// such a broadcast has, read from the window's own flags and never from the network. Read on the UI thread.
    /// </summary>
    internal BroadcastLegState BroadcastState =>
        _closing ? BroadcastLegState.Stopped
        : _broadcastEnded ? BroadcastLegState.Ended
        : _failureShown ? BroadcastLegState.Failed
        : _recoveryInFlight ? BroadcastLegState.Reconnecting
        : _reachedLive ? BroadcastLegState.Playing
        : BroadcastLegState.Connecting;
}
