using System.IO;
using System.Net.Http;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0031: the published channel-preview atlas. Offered after an explicit catalog update, downloaded
/// only on acceptance, and seeded once into the store the grid already reads from.
/// </summary>
public partial class MainWindow
{
    // Dedicated client: the sheet is ~11 MB and HttpClient.Timeout severs the body read even under
    // ResponseHeadersRead, so a shared 30 s timeout would cut a slow link mid-download. The infinite
    // timeout here is load-bearing rather than a workaround - SP-0056 made the service's idle bound the
    // real limit, and it can only act if nothing above it is counting wall-clock time.
    private readonly HttpClient _previewAtlasHttpClient = CreatePreviewAtlasHttpClient();

    // Per-session, deliberately not persisted. Latched only when the offer is ACCEPTED, so declining
    // never silences the feature: the next catalog update offers it again, which is also the way back
    // for a user who changes their mind.
    private bool _previewAtlasOfferLatched;
    private bool _previewAtlasImporting;

    private static HttpClient CreatePreviewAtlasHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        return client;
    }

    private bool ChannelPreviewAtlasEligible =>
        _previewFrameStore is not null &&
        !_previewAtlasOfferLatched &&
        !_previewAtlasImporting &&
        _state.ChannelPreviewAtlasRevision != ChannelPreviewAtlasService.Revision;

    /// <summary>Shows the offer after an explicit catalog update. The only path that leads to a download.</summary>
    private void MaybeOfferChannelPreviews()
    {
        if (!ChannelPreviewAtlasEligible)
        {
            return;
        }

        ChannelPreviewOfferBar.Visibility = Visibility.Visible;
    }

    private void ChannelPreviewOfferDismiss_Click(object sender, RoutedEventArgs e) =>
        ChannelPreviewOfferBar.Visibility = Visibility.Collapsed;

    private async void ChannelPreviewOfferAccept_Click(object sender, RoutedEventArgs e)
    {
        if (_previewFrameStore is null || _previewAtlasImporting)
        {
            return;
        }

        _previewAtlasOfferLatched = true;
        _previewAtlasImporting = true;
        _cancellableOperation = new CancellationTokenSource();
        _reportingProgress = true;
        ChannelPreviewOfferBar.Visibility = Visibility.Collapsed;
        // SP-0056: this operation never latched the busy state, so its bar was never shown (visibility is
        // SetBusy's alone) and the operations menu stayed live through a multi-minute import - a catalog
        // refresh was reachable mid-import. Latching it is what makes a bar and a cancel button possible
        // here, and closes that hole as a side effect.
        SetBusy(true, cancellable: true);
        // The sidecar fetch runs before the sheet's first report, and it reports nothing by design, so this
        // is the honest line for that second: a byte count that has not started yet.
        SetStatus("ChannelPreviewsDownloadProgressUnknown", 0);
        try
        {
            var result = await ImportChannelPreviewsAsync();
            if (result.CodecUnavailable)
            {
                SetStatus("ChannelPreviewsUnavailable");
                return;
            }

            SetStatus("ChannelPreviewsDone", result.Seeded);
            // The grid caches its tiles at layout time, so an atlas that lands afterwards stays invisible
            // until the visible rows are re-queued from the store.
            if (IsGridMode && _previewCoordinator?.IsRunning == true)
            {
                await QueueVisibleSafelyAsync(force: false);
            }
        }
        // SP-0056: abandoning is not failing, so this precedes the general catch. OperationCanceledException
        // stays in that catch's filter too, because the sidecar deadline can still raise one the user never
        // asked for. Tiles already written stay in the store: they are valid pictures for their channels and
        // the store is a cache the grid reads opportunistically, so discarding completed work would buy
        // nothing.
        catch (OperationCanceledException) when (_cancellableOperation?.IsCancellationRequested == true)
        {
            _log.Event("CANCEL", "op=preview_atlas");
            SetStatus("ChannelPreviewsCancelled");
            // Releasing the latch is what makes cancelling mean "not now" rather than "not this session".
            // The latch exists so an ACCEPTED import is not offered again (see the field's own note); a
            // user who stopped one - possibly by accident - is in the same position as one who pressed Not
            // now, and must be able to retry without restarting the application.
            _previewAtlasOfferLatched = false;
        }
        // InvalidOperationException covers WPF imaging state/affinity faults: this handler is async void,
        // so anything escaping it takes the whole app down - AC 6 requires a message, never a crash.
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or OperationCanceledException or TimeoutException or System.Text.Json.JsonException
            or InvalidOperationException)
        {
            // The revision marker is deliberately NOT written, so the offer returns on the next update.
            // A cancellation reaches the same outcome by the same route - it throws out of the import
            // before the marker is persisted - which is why abandoning also leaves the offer available.
            _log.Error("Channel preview atlas download failed", exception);
            SetStatus("ChannelPreviewsFailed");
        }
        finally
        {
            _previewAtlasImporting = false;
            _reportingProgress = false;
            SetBusy(false);
            _cancellableOperation?.Dispose();
            _cancellableOperation = null;
        }
    }

    private async Task<ChannelPreviewImportResult> ImportChannelPreviewsAsync()
    {
        // Kept in its own method so the payload (the ~11 MB compressed sheet) and the decoded frame are
        // both unreachable before the reclaim below runs.
        var result = await RunChannelPreviewImportAsync();

        // SP-0056: the reclaim below takes seconds and cannot report, so the bar stops claiming a fraction
        // before it starts. Clearing the flag first is what stops a tile report still queued on the
        // dispatcher from overwriting this line - or, later, the outcome.
        _reportingProgress = false;
        CatalogProgress.IsIndeterminate = true;
        SetStatus("ChannelPreviewsFinishing");

        // The decoded sheet is a ~235 MB WIC bitmap whose native memory is released through a finalizer,
        // so nothing reclaims it on its own: measured 786 MB resident after the import against 289 MB
        // before it. A forced reclaim is normally the wrong tool, but this is the case it exists for -
        // a one-shot, user-initiated bulk operation that allocated hundreds of megabytes of
        // finalizable, natively-backed objects and will not repeat this session.
        await Task.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });

        if (!result.CodecUnavailable)
        {
            _state = await PersistAsync(_state with
            {
                ChannelPreviewAtlasRevision = ChannelPreviewAtlasService.Revision
            });
        }

        return result;
    }

    private async Task<ChannelPreviewImportResult> RunChannelPreviewImportAsync()
    {
        var token = _cancellableOperation?.Token ?? CancellationToken.None;
        var download = OnDispatcher<DownloadProgress>(report => ShowDownloadProgress(
            report,
            "ChannelPreviewsDownloadProgress",
            "ChannelPreviewsDownloadProgressUnknown",
            // The completion line covers the decode too: it starts inside the importer before the first
            // tile report lands, and a ~235 MB bitmap does not decode instantly.
            "ChannelPreviewsDecoding"));
        var payload = await new ChannelPreviewAtlasService(_previewAtlasHttpClient).DownloadAsync(download, token);
        var catalogUrls = _state.Channels.Select(channel => channel.Url).ToHashSet(StringComparer.Ordinal);
        var importer = new ChannelPreviewAtlasImporter(_previewFrameStore!, _log);
        var tiles = new Progress<(int Processed, int Total)>(report =>
            ShowCountProgress(report.Processed, report.Total, "ChannelPreviewsWorking"));
        // One Task.Run, one thread: the decode and every crop must stay on the thread that owns the decoder.
        return await Task.Run(() => importer.Import(payload, catalogUrls, tiles, token), token);
    }
}
