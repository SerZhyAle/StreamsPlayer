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
    // Dedicated client: the sheet is ~11 MB and the shared catalog client's 30 s timeout would cut a slow
    // link mid-download. The real bound is the service's own deadline.
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
        ChannelPreviewOfferBar.Visibility = Visibility.Collapsed;
        SetStatus("ChannelPreviewsWorking", 0);
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
        // InvalidOperationException covers WPF imaging state/affinity faults: this handler is async void,
        // so anything escaping it takes the whole app down - AC 6 requires a message, never a crash.
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // The revision marker is deliberately NOT written, so the offer returns on the next update.
            _log.Error("Channel preview atlas download failed", exception);
            SetStatus("ChannelPreviewsFailed");
        }
        finally
        {
            _previewAtlasImporting = false;
        }
    }

    private async Task<ChannelPreviewImportResult> ImportChannelPreviewsAsync()
    {
        // Kept in its own method so the payload (the ~11 MB compressed sheet) and the decoded frame are
        // both unreachable before the reclaim below runs.
        var result = await RunChannelPreviewImportAsync();

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
            _state = await _store.SaveAsync(_state with
            {
                ChannelPreviewAtlasRevision = ChannelPreviewAtlasService.Revision
            });
        }

        return result;
    }

    private async Task<ChannelPreviewImportResult> RunChannelPreviewImportAsync()
    {
        var payload = await new ChannelPreviewAtlasService(_previewAtlasHttpClient).DownloadAsync();
        var catalogUrls = _state.Channels.Select(channel => channel.Url).ToHashSet(StringComparer.Ordinal);
        var importer = new ChannelPreviewAtlasImporter(_previewFrameStore!, _log);
        var progress = new Progress<int>(seeded => SetStatus("ChannelPreviewsWorking", seeded));
        // One Task.Run, one thread: the decode and every crop must stay on the thread that owns the decoder.
        return await Task.Run(() => importer.Import(payload, catalogUrls, progress, CancellationToken.None));
    }
}
