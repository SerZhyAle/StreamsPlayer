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

    internal PlaybackFailureDialog(string channelTitle, SourceOrigin origin, string report)
    {
        InitializeComponent();
        _report = report;
        _origin = origin;
        MessageText.Text = LocalizationService.Format("FailureDialogMessage", StreamTitleFormatter.Display(channelTitle));
        RemoveButton.SetResourceReference(ContentControl.ContentProperty,
            origin == SourceOrigin.Catalog ? "FailureHide" : "FailureDelete");
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        Choice = PlaybackFailureChoice.Retry;
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_origin is SourceOrigin.Manual or SourceOrigin.Imported)
        {
            var confirm = MessageBox.Show(
                this,
                LocalizationService.Get("FailureConfirmDelete"),
                LocalizationService.Get("StreamUnavailableTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
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
