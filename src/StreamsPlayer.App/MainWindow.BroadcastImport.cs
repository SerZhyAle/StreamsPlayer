using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// 196 section 6.2: the broadcast-resource import surfaces (`.spres`), and 196 section 6.3: the
/// playback pre-flight of a broadcast row - the access-path race and the playlist fetch. Parsing,
/// composition and de-duplication live in Core; this owns Windows I/O, the catalog state and consent.
/// </summary>
public partial class MainWindow
{
    /// <summary>The M3U body of a folder broadcast is bounded by the snapshot it lists; this is the
    /// same ceiling the URL import applies to a downloaded playlist.</summary>
    private const long MaximumPlaylistBodyBytes = 2 * 1024 * 1024;

    /// <summary>S12-3: the wall clock of one playlist fetch, head and body together - the old client timeout's 30 seconds.</summary>
    private static readonly TimeSpan PlaylistFetchDeadline = TimeSpan.FromSeconds(30);

    private async Task ImportBroadcastResourceFileAsync(string path, Window owner, bool playAfterImport = false)
    {
        string text;
        try
        {
            // Off the UI thread, with the same ceiling the reader faces: an oversized read is one that
            // could not be imported, not a document.
            var filePath = path;
            text = await Task.Run(async () =>
            {
                var info = new FileInfo(filePath);
                if (info.Length > BroadcastResourceReader.MaximumDocumentBytes)
                {
                    return string.Empty;
                }

                return await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Event("RESOURCE IMPORT FAIL", "source=file", $"reason={exception.GetType().Name}");
            MessageBox.Show(owner, LocalizationService.Get("ImportFileReadFailed"),
                LocalizationService.Get("BroadcastResourceTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ImportBroadcastResourceTextAsync(text, owner, playAfterImport);
    }

    private async Task ImportBroadcastResourceTextAsync(string text, Window owner, bool playAfterImport = false)
    {
        if (_busy)
        {
            _log.Event("REFUSE", "op=resource_import", "reason=busy");
            MessageBox.Show(owner, LocalizationService.Get("BroadcastResourceBusy"),
                LocalizationService.Get("BroadcastResourceTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var read = BroadcastResourceReader.Read(text);
        if (!read.IsAccepted || read.Resource is not { } resource)
        {
            var key = read.Status switch
            {
                BroadcastResourceStatus.UnsupportedVersion => "BroadcastResourceUnsupportedVersion",
                BroadcastResourceStatus.NotResourceDocument => "BroadcastResourceNotDocument",
                _ => "BroadcastResourceInvalid"
            };
            _log.Event("RESOURCE IMPORT FAIL", $"status={read.Status}");
            MessageBox.Show(owner, LocalizationService.Get(key),
                LocalizationService.Get("BroadcastResourceTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // One row per broadcast: the resource path is the de-duplication key, so a re-imported
        // refreshed document updates the row instead of adding a second one.
        // S12-2: applied to the state the commit hands in, as the sibling imports do - a list built from
        // _state outside the commit would write back over a pin, a play outcome or a directory mark that
        // was committed (or still in flight) in between.
        StreamChannel? imported = null;
        var added = false;
        if (!await TryPersistAsync(state =>
            {
                var result = BroadcastChannelImport.Apply(state.Channels, resource, DateTimeOffset.UtcNow);
                var rows = result.Channels;
                var placed = result.Channel;
                if (result.Added)
                {
                    // An added row takes its place at the end of the list, like every other import.
                    var nextOrder = state.Channels.Count == 0 ? 0 : state.Channels.Max(row => row.SortIndex) + 1;
                    placed = placed with { SortIndex = nextOrder };
                    rows = rows.Select(row => ReferenceEquals(row, result.Channel) ? placed : row).ToList();
                }

                imported = placed;
                added = result.Added;
                return state with { Channels = [.. rows] };
            }))
        {
            return;
        }

        if (imported is not { } channel)
        {
            return;
        }

        _log.Event("RESOURCE IMPORT APPLY",
            $"added={added}",
            $"playlist={channel.IsPlaylistResource}",
            $"addresses={1 + (channel.AlternateUrls?.Count ?? 0)}");
        PopulateFacets();
        ApplyFilter();
        SetStatus(added ? "BroadcastResourceAdded" : "BroadcastResourceUpdated", channel.Title);
        await RevealChannelAsync(channel.Id);
        if (playAfterImport)
        {
            await PlayChannelAsync(channel, rememberSelection: false);
        }
    }

    /// <summary>
    /// 196 section 6.3: a broadcast row's playback pre-flight. Races every stored address and returns
    /// the channel pinned to the winner (the ordinary flow continues from there); a playlist channel
    /// additionally fetches its M3U from the winning address and answers with the item URLs, which play
    /// in order inside one window. A dead broadcast - nothing answered, or the playlist could not be
    /// fetched - is reported here with the right sentence and answers null; the row stays, marked Fail.
    /// </summary>
    private async Task<(StreamChannel? Channel, IReadOnlyList<string>? PlaylistItems)> ResolveBroadcastPlaybackAsync(
        StreamChannel channel,
        bool quiet)
    {
        if (channel.ResourceExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            await ReportBroadcastUnavailableAsync(channel, quiet);
            return (null, null);
        }

        var candidates = BroadcastUrl.CandidatesOf(channel);
        var winner = await BroadcastUrl.RaceAsync(_httpClient, candidates);
        if (winner is null)
        {
            await ReportBroadcastUnavailableAsync(channel, quiet);
            return (null, null);
        }

        var effective = channel with { Url = winner };
        if (!channel.IsPlaylistResource)
        {
            return (effective, null);
        }

        var items = await FetchPlaylistItemsAsync(winner);
        if (items is null || items.Count == 0)
        {
            await ReportBroadcastUnavailableAsync(channel, quiet);
            return (null, null);
        }

        return (effective, items);
    }

    /// <summary>
    /// Fetches a folder broadcast's M3U body from the winning address and parses its items with the
    /// existing <see cref="M3uPlaylistParser"/> - the gateway deliberately emits a plain
    /// <c>#EXTINF</c> list with no <c>#EXT-X-</c> tags, precisely because that parser refuses HLS
    /// manifests. Null when the body could not be fetched or read.
    /// </summary>
    private async Task<IReadOnlyList<string>?> FetchPlaylistItemsAsync(string url)
    {
        try
        {
            // S12-3: HttpClient.Timeout covers only the response head (SP-0129), so a gateway that stalls
            // after its headers held this play flow open with no progress and no cancel. The head gets the
            // helper's bound, the body a silence bound, and the whole fetch one wall-clock deadline; any
            // expiry ends as the same null a failed fetch returns.
            using var deadline = new CancellationTokenSource(PlaylistFetchDeadline);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await HttpDownload.SendForHeadersAsync(
                _httpClient, request, M3uImportService.DefaultHeaderTimeout, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                _log.Event("PLAYLIST FETCH", $"status={(int)response.StatusCode}", $"url={url}");
                return null;
            }

            var bytes = await HttpDownload.ReadAllBytesAsync(
                response, progress: null, MaximumPlaylistBodyBytes, M3uImportService.DefaultIdleTimeout, deadline.Token);
            var body = Encoding.UTF8.GetString(bytes);
            // Analyze (not Parse) keeps the intent of the import contract: the items are played, not
            // stored - every entry is already this broadcast's own item.
            var preview = await Task.Run(() => M3uPlaylistParser.Analyze(body, new HashSet<string>(StringComparer.Ordinal)));
            return preview.Status == M3uImportStatus.Ok
                ? preview.NewEntries.Select(entry => entry.Url).ToArray()
                : null;
        }
        catch (InvalidDataException)
        {
            // HttpDownload refuses a body above the ceiling, declared or actual.
            _log.Event("PLAYLIST FETCH", "reason=too_large", $"url={url}");
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                              or OperationCanceledException or TimeoutException)
        {
            _log.Event("PLAYLIST FETCH", $"reason={exception.GetType().Name}", $"url={url}");
            return null;
        }
    }

    /// <summary>
    /// 196 section 6.3: a dead broadcast is a clear sentence, not a broken row. The row stays, marked
    /// <see cref="PlayOutcome.Fail"/> - the player never deletes a user's row on its own - and the
    /// sentence carries the document's accessNote when it had one.
    /// </summary>
    private async Task ReportBroadcastUnavailableAsync(StreamChannel channel, bool quiet)
    {
        _log.Event("PLAYBACK UNAVAILABLE", $"resource={channel.ResourceKey}");
        await RecordPlayOutcome(channel.Id, succeeded: false);
        var sentence = LocalizationService.Format(
            "BroadcastNoLongerAvailable", StreamTitleFormatter.Display(channel.Title));
        if (!string.IsNullOrWhiteSpace(channel.ResourceAccessNote))
        {
            sentence += Environment.NewLine + channel.ResourceAccessNote;
        }

        if (quiet)
        {
            SetStatus("BroadcastNoLongerAvailable", StreamTitleFormatter.Display(channel.Title));
            return;
        }

        if (IsCompact)
        {
            SetStatus("BroadcastNoLongerAvailable", StreamTitleFormatter.Display(channel.Title));
            return;
        }

        MessageBox.Show(this, sentence,
            LocalizationService.Get("BroadcastResourceTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
