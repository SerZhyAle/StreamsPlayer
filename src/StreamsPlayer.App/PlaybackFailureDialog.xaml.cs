using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal enum PlaybackFailureChoice
{
    None,
    Retry,
    Remove
}

// SP-0020: actionable replacement for the dead-end failure MessageBox. Offers Retry, an origin-aware
// Remove (Hide for catalog rows, Delete for user rows), Copy report, and Keep. Removal is confirmed for
// the irreversible user-row delete; hide is reversible via the manage-hidden view, so it needs no re-confirm.
public partial class PlaybackFailureDialog : Window
{
    private readonly string _report;
    private readonly SourceOrigin _origin;

    internal PlaybackFailureChoice Choice { get; private set; } = PlaybackFailureChoice.None;

    /// <param name="message">
    /// SP-0099: a source that knows why it ended says so instead of the generic "could not be played".
    /// </param>
    /// <param name="canRetry">
    /// SP-0124: false when a retry cannot change the answer - an address that is never launched stays so.
    /// </param>
    /// <param name="reachability">
    /// SP-0041: what the connectivity gate found before the verdict. The default is the dialog as it was.
    /// </param>
    internal PlaybackFailureDialog(
        string channelTitle,
        SourceOrigin origin,
        string report,
        ChannelAccess access,
        string? message = null,
        bool canRetry = true,
        PlaybackReachability reachability = PlaybackReachability.NotProbed)
    {
        InitializeComponent();
        _report = report;
        _origin = origin;
        MessageText.Text = message ?? LocalizationService.Format("FailureDialogMessage", StreamTitleFormatter.Display(channelTitle));
        if (!canRetry)
        {
            RetryButton.IsDefault = false;
            RetryButton.Visibility = Visibility.Collapsed;
            KeepButton.IsDefault = true; // never the destructive Remove
        }

        RemoveButton.SetResourceReference(ContentControl.ContentProperty,
            ChannelOwnership.IsBankSourced(origin) ? "FailureHide" : "FailureDelete");
        // SP-0041 Decision 4: a channel that was never reached has not been proven broken, and for a
        // MANUAL/IMPORTED row the delete is irreversible - so the offer is withheld, and the line says why.
        // The Core rule decides; this dialog does not re-derive it. Retry stays the default and Keep the
        // cancel, so the keyboard path does not depend on the hidden button.
        RemoveButton.Visibility = PlaybackReachabilityRules.AllowsChannelRemoval(reachability)
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoNetworkText.Visibility = reachability == PlaybackReachability.NetworkUnreachable
            ? Visibility.Visible
            : Visibility.Collapsed;
        // SP-0033: the tag explains a failure, so it is only ever shown on this path - a region-locked
        // channel that plays says nothing.
        RegionRestrictedText.Visibility = access == ChannelAccess.GeoRestricted
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        Choice = PlaybackFailureChoice.Retry;
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        // SP-0177: every delete is confirmed - by the same rule that labels the button, so no origin can
        // reach "Delete" without the question.
        if (!ChannelOwnership.IsBankSourced(_origin))
        {
            var confirm = MessageBox.Show(
                this,
                LocalizationService.Get("FailureConfirmDelete"),
                LocalizationService.Get("StreamUnavailableTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Choice = PlaybackFailureChoice.Remove;
        DialogResult = true;
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_report);
            CopiedText.Visibility = Visibility.Visible;
        }
        catch (ExternalException)
        {
            // Clipboard was briefly locked by another process; copying is best-effort and never transmits.
        }
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Choice = PlaybackFailureChoice.None;
        DialogResult = false;
    }
}
