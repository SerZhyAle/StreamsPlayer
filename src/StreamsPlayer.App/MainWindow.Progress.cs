using System.Windows;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0056: the shared status bar's determinate mode and the cancel affordance for the two downloads big
/// enough to need them - the stream catalog and the channel-preview sheet. <c>SetBusy</c> owns the bar's
/// visibility and resets it to the indeterminate default; everything here is what a *reporting* operation
/// adds on top.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The one cancellation source for both cancellable downloads. One field is enough because they are
    /// mutually exclusive: each latches <c>_busy</c> for its whole duration, and each entry point returns
    /// early when it is already set.
    /// </summary>
    private CancellationTokenSource? _cancellableOperation;

    /// <summary>
    /// Whether the status line still belongs to a running operation's progress.
    /// </summary>
    /// <remarks>
    /// A report queued on the dispatcher can otherwise land after the operation has already written its
    /// outcome and overwrite it - leaving "Applying the catalog.." on screen in place of the result. Cleared
    /// the moment the caller stops wanting reports, so a late one is dropped rather than raced against.
    /// </remarks>
    private bool _reportingProgress;

    /// <summary>
    /// Delivers a download's reports on the calling thread when that thread is already the dispatcher's.
    /// </summary>
    /// <remarks>
    /// Both downloads are awaited from the dispatcher and the core marshals nothing away from it, so a
    /// report raised mid-transfer can be applied synchronously. That is what makes the switch out of
    /// determinate mode land *before* the synchronous parse that follows the last chunk, rather than being
    /// queued behind it. The dispatcher check keeps it correct if that ever stops being true.
    /// </remarks>
    private IProgress<T> OnDispatcher<T>(Action<T> handler) => new DispatcherProgress<T>(Dispatcher, handler);

    private sealed class DispatcherProgress<T>(Dispatcher dispatcher, Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            if (dispatcher.CheckAccess())
            {
                handler(value);
                return;
            }

            dispatcher.InvokeAsync(() => handler(value), DispatcherPriority.Normal);
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        // Disabled rather than hidden: the row keeps its width while the read loop unwinds, and a second
        // click cannot re-enter. SetBusy(false) hides it once the operation has actually ended.
        CancelOperationButton.IsEnabled = false;
        _cancellableOperation?.Cancel();
    }

    /// <summary>
    /// Renders one download report onto the shared bar and the status line.
    /// </summary>
    /// <remarks>
    /// Reaching the declared length switches the bar back to its indeterminate appearance and names the
    /// step that follows. Nothing after a download can report - the catalog's parse and merge are
    /// synchronous, and the preview sheet's decode owns its thread - so a bar parked at 100% through
    /// seconds of real work would be the same lie as an animation that means nothing. The final report
    /// always carries the complete length, so this fires exactly once and needs no second channel from
    /// the core.
    /// </remarks>
    private void ShowDownloadProgress(
        DownloadProgress report,
        string percentKey,
        string unknownKey,
        string completionKey)
    {
        if (!_reportingProgress)
        {
            return;
        }

        if (report.Fraction is not { } fraction)
        {
            // No declared length. Both published assets do declare one, but the contract never promised
            // it, so the honest rendering is a byte count against an animated bar.
            SetStatus(unknownKey, report.ReceivedBytes / (1024 * 1024));
            return;
        }

        if (fraction >= 1 && report.ReceivedBytes > 0)
        {
            CatalogProgress.IsIndeterminate = true;
            SetStatus(completionKey);
            return;
        }

        CatalogProgress.IsIndeterminate = false;
        CatalogProgress.Value = fraction * 100;
        // Whole megabytes, as the components installer already renders them. The figure rounds down, so a
        // 7.2 MB download reads "7 of 7 MB" for its last stretch while the percentage still climbs: the
        // percentage and the bar carry the precision, and the byte counts are there to say how big the
        // thing is.
        SetStatus(
            percentKey,
            (int)(fraction * 100),
            report.ReceivedBytes / (1024 * 1024),
            report.TotalBytes!.Value / (1024 * 1024));
    }

    /// <summary>
    /// Shows a count of items processed against their total on the same bar. Used by the preview import's
    /// second phase, which is tile work rather than bytes.
    /// </summary>
    private void ShowCountProgress(int processed, int total, string statusKey)
    {
        if (!_reportingProgress || total <= 0)
        {
            return;
        }

        CatalogProgress.IsIndeterminate = false;
        CatalogProgress.Value = Math.Clamp(processed * 100.0 / total, 0, 100);
        SetStatus(statusKey, processed, total);
    }
}
