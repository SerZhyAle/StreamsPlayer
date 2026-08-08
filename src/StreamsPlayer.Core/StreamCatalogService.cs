namespace StreamsPlayer.Core;

public sealed class StreamCatalogService
{
    public const string CatalogUrl =
        "https://github.com/SerZhyAle/FastMediaSorter_mob_v2/releases/download/delivery-so-v1/stream-catalog.zip";

    // SP-0056: bounds silence, not duration. The single 30 s deadline this replaced covered the download
    // and every step after it, and was chosen when the bank was far smaller - finishing today's 7.2 MB
    // inside it demands a sustained 250 KB/s, so a link that was working the whole time failed. A slow
    // link now finishes; a socket that stops answering still fails promptly.
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(20);

    // The steps after the download are local work on a fixed-size input, so a duration bound is the right
    // shape there in a way it is not for a transfer.
    private static readonly TimeSpan ApplyDeadline = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly StreamCatalogStore _store;

    public StreamCatalogService(HttpClient httpClient, StreamCatalogStore store)
    {
        _httpClient = httpClient;
        _store = store;
    }

    public async Task<CatalogRefreshResult> RefreshAsync(
        CatalogState currentState,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // ceilingBytes is null deliberately: this class has never bounded the ZIP's size, and introducing
        // a ceiling is a behaviour change with its own failure mode rather than part of reporting. Passing
        // the argument explicitly is what keeps the omission legible.
        var bytes = await HttpDownload.ReadAllBytesAsync(
            response, progress, ceilingBytes: null, DownloadIdleTimeout, cancellationToken);

        // Nothing below writes to the store until SaveAsync, so an abandoned refresh already left the
        // catalog untouched - but checking here makes that an asserted property instead of a coincidence,
        // and it stops a cancelled refresh from spending a second of the caller's thread on the parse.
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ApplyDeadline);
        using var stream = new MemoryStream(bytes, writable: false);
        var bank = StreamBankReader.Read(stream);
        if (bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The downloaded catalog contains no valid channels.");
        }

        var now = DateTimeOffset.UtcNow;
        var merge = CatalogMerger.Merge(currentState.Channels, bank.Entries, now);
        var channels = merge.Channels.ToList();
        var state = currentState with
        {
            Channels = channels,
            LastCatalogRefreshAt = now
        };

        // SP-0052: a refresh reclaims every snapshot row onto its own atlas, so once none is left the
        // bundled atlas is a file nothing points at - seven megabytes of the user's disk that no later
        // save would ever reclaim, because the store keeps whatever the state still names. Dropping the
        // reference here is what lets its own cleanup pass sweep the file. The date goes with it: it
        // describes rows that no longer exist.
        if (!channels.Any(channel => channel.FaviconSource == FaviconSource.Snapshot))
        {
            state = state with { SnapshotAtlasFileName = null, AppliedSnapshotDate = null };
        }
        // Only a bank that actually carried an atlas may replace the stored one. Passing replaceAtlas
        // unconditionally made a bank without an atlas - a mispackaged upstream zip, or one whose atlas this
        // reader rejected - delete the installed atlas, so a single refresh silently stripped the favicon
        // from every channel with no error and no way back short of a corrected republish.
        state = await _store.SaveAsync(
            state,
            bank.FaviconAtlas,
            replaceAtlas: bank.FaviconAtlas is { Length: > 0 },
            deadline.Token);
        return new CatalogRefreshResult(state, merge.Added, merge.Updated, merge.Removed);
    }
}
