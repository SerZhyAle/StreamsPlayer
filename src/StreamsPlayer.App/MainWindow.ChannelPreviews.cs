using System.IO;
using System.Net.Http;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0031: the published channel-preview artwork. Offered after an explicit catalog update, downloaded
/// only on acceptance, and seeded once into the store the grid already reads from.
/// </summary>
public partial class MainWindow
{
    // Dedicated client: the tile pack is ~15 MB and HttpClient.Timeout severs the body read even under
    // ResponseHeadersRead, so a shared 30 s timeout would cut a slow link mid-download. The infinite
    // timeout here is load-bearing rather than a workaround - SP-0056 made the service's idle bound the
    // real limit, and it can only act if nothing above it is counting wall-clock time.
    private readonly HttpClient _previewArtworkHttpClient = CreatePreviewArtworkHttpClient();

    private bool _previewArtworkImporting;

    private static HttpClient CreatePreviewArtworkHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        return client;
    }

    /// <summary>
    /// Asks after every completed catalog import, and is the only path that leads to a download.
    /// </summary>
    /// <remarks>
    /// SP-0088: a modal question, asked every time, replacing the inline bar and the revision gate that
    /// used to sit in front of it. The gate meant the offer appeared at most once per published
    /// revision - so a machine that had already imported was never asked again, however many channels
    /// a later catalog brought in, and bumping a compiled-in revision constant was the only way to
    /// re-open it. Asking every time makes the answer the user's each time.
    /// <para>
    /// SP-0091: what gets written after a successful import is now the manifest stamp of the build that
    /// landed. It records what was installed; nothing reads it to decide whether to ask.
    /// </para>
    /// </remarks>
    private async Task OfferChannelPreviewsAsync()
    {
        if (_previewFrameStore is null || _previewArtworkImporting)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"{LocalizationService.Get("ChannelPreviewsOffer")}{Environment.NewLine}{Environment.NewLine}" +
                LocalizationService.Get("ChannelPreviewsOfferTip"),
            LocalizationService.Get("ChannelPreviewsDownload"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        _log.Event("CHANNEL PREVIEWS", "op=offer",
            $"result={(answer == MessageBoxResult.Yes ? "accepted" : "declined")}");
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await ImportChannelPreviewsInteractiveAsync();
    }

    private async Task ImportChannelPreviewsInteractiveAsync()
    {
        _previewArtworkImporting = true;
        _cancellableOperation = new CancellationTokenSource();
        _reportingProgress = true;
        // SP-0056: this operation never latched the busy state, so its bar was never shown (visibility is
        // SetBusy's alone) and the operations menu stayed live through a multi-minute import - a catalog
        // refresh was reachable mid-import. Latching it is what makes a bar and a cancel button possible
        // here, and closes that hole as a side effect.
        SetBusy(true, cancellable: true);
        // The manifest and sidecar fetches run before the pack's first report, and they report nothing by
        // design, so this is the honest line for that second: a byte count that has not started yet.
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
            // The grid caches its tiles at layout time, so artwork that lands afterwards stays invisible
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
            _log.Event("CANCEL", "op=preview_artwork");
            SetStatus("ChannelPreviewsCancelled");
        }
        // InvalidOperationException covers WPF imaging state/affinity faults: this handler is async void,
        // so anything escaping it takes the whole app down - AC 6 requires a message, never a crash.
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or OperationCanceledException or TimeoutException or System.Text.Json.JsonException
            or InvalidOperationException)
        {
            // The artwork stamp is deliberately NOT written: it must record what actually landed, and a
            // failed import landed nothing. A cancellation reaches the same outcome by the same route,
            // throwing out of the import before the stamp is persisted. A manifest mismatch arrives here
            // too, as InvalidDataException - a half-replaced publish is a retry, not a state change.
            _log.Error("Channel preview artwork download failed", exception);
            SetStatus("ChannelPreviewsFailed");
        }
        finally
        {
            _previewArtworkImporting = false;
            _reportingProgress = false;
            SetBusy(false);
            _cancellableOperation?.Dispose();
            _cancellableOperation = null;
        }
    }

    private async Task<ChannelPreviewImportResult> ImportChannelPreviewsAsync()
    {
        var token = _cancellableOperation?.Token ?? CancellationToken.None;
        var download = OnDispatcher<DownloadProgress>(report => ShowDownloadProgress(
            report,
            "ChannelPreviewsDownloadProgress",
            "ChannelPreviewsDownloadProgressUnknown",
            // The completion line covers opening and verifying the pack: both happen before the first
            // tile report lands.
            "ChannelPreviewsDecoding"));
        var artwork = await new ChannelPreviewArtworkService(_previewArtworkHttpClient).DownloadAsync(download, token);
        var catalogUrls = _state.Channels.Select(channel => channel.Url).ToHashSet(StringComparer.Ordinal);
        var importer = new ChannelPreviewImporter(_previewFrameStore!, _log);
        var tiles = new Progress<(int Processed, int Total)>(report =>
            ShowCountProgress(report.Processed, report.Total, "ChannelPreviewsWorking"));
        // One Task.Run, one thread: the pack's entries are read through one shared archive stream.
        var result = await Task.Run(() => importer.Import(artwork, catalogUrls, tiles, token), token);

        // SP-0091: this used to be followed by a forced GC.Collect/WaitForPendingFinalizers pair and a
        // "finishing up" line, because the sheet path left a ~235 MB finalizable WIC bitmap behind
        // (measured 786 MB resident after an import against 289 MB before it). The tile pack decodes one
        // 240x135 frame at a time, so there is nothing left to reclaim and nothing worth telling the user
        // about - the honest end of the operation is now its result line.
        if (!result.CodecUnavailable)
        {
            _state = await PersistAsync(_state with { ChannelPreviewArtworkStamp = artwork.Stamp });
        }

        return result;
    }
}
