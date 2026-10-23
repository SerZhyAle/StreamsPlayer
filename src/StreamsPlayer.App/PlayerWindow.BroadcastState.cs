using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class PlayerWindow
{
    private void ShowBroadcastFailure(string reason)
    {
        var row = ExchangeFailurePresentation.BroadcastRow(reason)
            ?? (ExchangeBroadcastCertificateException.MessageKey(reason) is null && ExchangeBroadcastAuth.MessageKey(reason) is null
                ? ExchangeFailureRows.Unavailable : null);
        BroadcastFailureText.Text = row is not null ? ExchangeFailurePresentation.Text(row)
            : LocalizationService.Get(ExchangeBroadcastCertificateException.MessageKey(reason)
                ?? ExchangeBroadcastAuth.MessageKey(reason) ?? IncomingBroadcastFailure.NoLongerAvailableKey);
        BroadcastFailureDetails.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        BroadcastFailureCode.Text = row is null ? "" : LocalizationService.Format("ExchangeDiagnosisCodeLine", row.Code);
        BroadcastFailurePanel.Visibility = Visibility.Visible;
        if (row is not null)
        {
            _log.Event("BROADCAST FAILURE", $"code={row.Code}");
        }
    }

    private void BroadcastRetry_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(BroadcastRetry_Click), async () =>
    {
        if (_closing || !_failureShown)
        {
            return;
        }

        BroadcastFailurePanel.Visibility = Visibility.Collapsed;
        _recovery.Reset();
        _recovering = false;
        DropRememberedCeilingOnMiss();
        _broadcastCredentials?.ResolveForRetry(_channel.Id);
        await RetryAfterFailureAsync();
    });

    /// <summary>
    /// SP-0222: what the broadcast list shows for a video broadcast opened in this window - the only live state
    /// such a broadcast has, read from the window's own flags and never from the network. Read on the UI thread.
    /// </summary>
    internal BroadcastLegState BroadcastState =>
        _closing ? BroadcastLegState.Stopped
        : _failureShown ? BroadcastLegState.Failed
        : _recoveryInFlight ? BroadcastLegState.Reconnecting
        : _reachedLive ? BroadcastLegState.Playing
        : BroadcastLegState.Connecting;
}
