using System.IO.Compression;

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

    // SP-0129: the silence bound starts only once the body is being read, and the catalog client has no
    // timeout of its own, so a TLS or proxy stall before the head left "Downloading catalog" spinning until
    // Cancel. A head is a few hundred bytes; this long without one is a dead connection.
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    // The steps after the download are local work on a fixed-size input, so a duration bound is the right
    // shape there in a way it is not for a transfer.
    private static readonly TimeSpan ApplyDeadline = TimeSpan.FromSeconds(60);

    /// <summary>Largest catalog archive this client will accept.</summary>
    /// <remarks>
    /// SP-0069: read this as a ceiling, never as a size - the bank is republished without an app release
    /// and grows with the channel count (2 361 rows once, 19 855 in 2026-08). The live artifact was
    /// 7.2 MB when this was written, so the headroom is deliberate and large; what it rules out is the
    /// mis-published or hostile response, which until now was pulled whole into a byte[] with
    /// OutOfMemoryException as the only backstop. Every other network payload in the product already had
    /// such a bound - <see cref="StreamBankReader.MaximumAtlasBytes"/> and
    /// <see cref="ChannelPreviewArtworkService.MaximumTilePackBytes"/> - and the catalog, the one payload
    /// always downloaded, was the exception.
    /// Unlike the atlas cap, exceeding this is an error rather than a silent drop: there is no catalog
    /// without the archive, so continuing would mean reporting success over an empty result.
    /// </remarks>
    public const long MaximumArchiveBytes = 128L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly StreamCatalogStore _store;
    private readonly PublishWindowRetry _publishWindowRetry;

    public StreamCatalogService(HttpClient httpClient, StreamCatalogStore store)
        : this(httpClient, store, PublishWindowRetry.Default)
    {
    }

    /// <summary>Tests substitute a schedule without real pauses; the product always uses the default.</summary>
    internal StreamCatalogService(HttpClient httpClient, StreamCatalogStore store, PublishWindowRetry publishWindowRetry)
    {
        _httpClient = httpClient;
        _store = store;
        _publishWindowRetry = publishWindowRetry;
    }

    /// <param name="retrying">
    /// SP-0107: told when the bank is caught mid-publish and the fetch is about to be repeated, so the
    /// caller can say so instead of leaving a stalled progress line.
    /// </param>
    public async Task<CatalogRefreshResult> RefreshAsync(
        CatalogState currentState,
        IProgress<DownloadProgress>? progress = null,
        IProgress<PublishWindowRetryNotice>? retrying = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await DownloadAsync(progress, retrying, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ApplyDeadline);
        var result = outcome.Apply(currentState);
        var state = await _store.SaveAsync(
            result.State,
            outcome.Bank.FaviconAtlas,
            outcome.ReplacesAtlas,
            deadline.Token);
        return result with { State = state };
    }

    /// <summary>
    /// Downloads and validates one published bank without observing or writing local catalog state.
    /// Its result can therefore be merged only after the caller has acquired its serialized state commit.
    /// </summary>
    public async Task<CatalogRefreshOutcome> DownloadAsync(
        IProgress<DownloadProgress>? progress = null,
        IProgress<PublishWindowRetryNotice>? retrying = null,
        CancellationToken cancellationToken = default)
    {
        // SP-0107, STREAM-BANK rule 11: the retry covers the fetch and the read of the archive and nothing
        // after them. Everything that could touch the user's catalog - the merge, item D's absence
        // handling, the save - runs only on a bank that arrived whole, so a publish window can at worst
        // cost the user a few seconds of waiting, never a row.
        var bank = await _publishWindowRetry.RunAsync(
            token => FetchBankAsync(progress, token), retrying, cancellationToken);

        // Nothing below writes to the store until SaveAsync, so an abandoned refresh already left the
        // catalog untouched - but checking here makes that an asserted property instead of a coincidence,
        // and it stops a cancelled refresh from spending a second of the caller's thread on the merge.
        cancellationToken.ThrowIfCancellationRequested();
        if (bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The downloaded catalog contains no valid channels.");
        }

        return new CatalogRefreshOutcome(bank, DateTimeOffset.UtcNow);
    }

    /// <summary>One attempt at the archive: request, bounded download, and the read of the ZIP.</summary>
    private async Task<StreamBank> FetchBankAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
        using var response = await HttpDownload.SendForHeadersAsync(_httpClient, request, HeaderTimeout, cancellationToken);
        response.EnsureSuccessStatusCode();
        // SP-0069 closed the omission that SP-0056 recorded here: bounding the archive is a behaviour
        // change, which is why it did not belong in a reporting ticket, and it belongs in a resilience one.
        var bytes = await HttpDownload.ReadAllBytesAsync(
            response, progress, MaximumArchiveBytes, DownloadIdleTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // SP-0069: the stream holds the archive array, so it is scoped to the read - a longer-lived one
        // kept several megabytes on the large-object heap rooted through the merge, the save and the
        // serialization, the most allocation-heavy stretch of the refresh. SP-0107 made the scope a method
        // so the read of the ZIP sits inside the retry: a truncated archive is one of the outcomes the
        // publish window produces.
        using var stream = new MemoryStream(bytes, writable: false);
        EnsureArchiveIsWhole(stream);
        return StreamBankReader.Read(stream);
    }

    /// <summary>
    /// SP-0107: a ZIP whose directory cannot be read at all is what a body cut off mid-publish looks like
    /// when HTTP itself did not notice - the central directory sits at the end, so it is the first thing
    /// truncation destroys. Checked here rather than inside <see cref="StreamBankReader"/>, whose other
    /// callers (the bundled snapshot, a file the user imports) read local bytes no publish can cut short
    /// and rely on its single <see cref="InvalidDataException"/>. Every refusal after this point - an
    /// empty archive, a misplaced or malformed CSV - describes a bank that arrived whole and is wrong, and
    /// stays an error on the first attempt.
    /// </summary>
    private static void EnsureArchiveIsWhole(MemoryStream stream)
    {
        try
        {
            using var _ = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new TruncatedArchiveException("The downloaded catalog ZIP is truncated or unreadable.", exception);
        }

        stream.Position = 0;
    }
}
