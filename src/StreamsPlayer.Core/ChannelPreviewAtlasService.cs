namespace StreamsPlayer.Core;

/// <summary>The downloaded channel-preview payload: the sprite sheet bytes plus its url -> tile map.</summary>
public sealed record ChannelPreviewAtlasPayload(byte[] Sheet, IReadOnlyDictionary<string, int> Coords);

/// <summary>
/// SP-0031 downloader for the published channel-preview atlas. Unlike the stream catalog, this is a
/// separate pair of release assets with its own lifecycle - it is deliberately not bundled into
/// <c>stream-catalog.zip</c> because it is an order of magnitude larger than the CSV.
/// Called only from an explicitly accepted user offer; there is no automatic or background fetch.
/// </summary>
public sealed class ChannelPreviewAtlasService
{
    /// <summary>
    /// The element revision pinned in both asset URLs. The publisher ships a tile-incompatible rebuild
    /// under a new suffix so an older client keeps resolving the sheet it was built against; nothing
    /// auto-upgrades, so bumping this constant is a deliberate code change.
    /// </summary>
    public const string Revision = "v1";

    public const string AtlasUrl =
        "https://github.com/SerZhyAle/FastMediaSorter_mob_v2/releases/download/delivery-so-v1/channel-preview-atlas-v1.webp";

    public const string CoordsUrl =
        "https://github.com/SerZhyAle/FastMediaSorter_mob_v2/releases/download/delivery-so-v1/channel-preview-coords-v1.json";

    /// <summary>
    /// Hard ceiling on the sheet held in memory. The publisher's own packer guard caps the atlas at
    /// 30 MiB; this leaves headroom for a future larger catalog while still refusing to pull an
    /// unbounded mispublished asset into a byte array.
    /// </summary>
    public const int MaximumSheetBytes = 48 * 1024 * 1024;

    // SP-0056: the sidecar is 135 KB. If it has not arrived in this long, the publish or the link is
    // broken, and failing on the cheap half before the multi-megabyte sheet is the point of fetching it
    // first.
    private static readonly TimeSpan SidecarDeadline = TimeSpan.FromSeconds(30);

    // SP-0056: the sheet gets no total-duration bound at all - this is it. The five-minute wall that used
    // to cover both fetches was "sized for a slow link", which is the job a silence bound does properly:
    // an 11 MB asset over a slow line now finishes however long it takes, while a socket that stops
    // answering fails in twenty seconds instead of after five minutes the user cannot tell from a hang.
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;

    public ChannelPreviewAtlasService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ChannelPreviewAtlasPayload> DownloadAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Sidecar first: at ~135 KB it is the cheap half, so a broken or missing publish fails before
        // the multi-megabyte sheet is pulled. It reports no progress for the same reason - a fetch that
        // completes in well under a second would flick a bar to full and back before the real download
        // even starts.
        using var sidecar = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sidecar.CancelAfter(SidecarDeadline);
        using var coordsResponse = await _httpClient.GetAsync(CoordsUrl, sidecar.Token);
        coordsResponse.EnsureSuccessStatusCode();
        var coordsJson = await coordsResponse.Content.ReadAsStringAsync(sidecar.Token);
        var coords = ChannelPreviewCoords.Parse(coordsJson);
        if (coords.Count == 0)
        {
            throw new InvalidDataException("The channel-preview sidecar lists no tiles.");
        }

        using var sheetResponse = await _httpClient.GetAsync(AtlasUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        sheetResponse.EnsureSuccessStatusCode();
        // The ceiling is checked against the declared length and against the bytes that actually arrive;
        // both live in the download loop now, so doing either here would leave one condition with two
        // messages.
        var sheet = await HttpDownload.ReadAllBytesAsync(
            sheetResponse, progress, MaximumSheetBytes, DownloadIdleTimeout, cancellationToken);
        return new ChannelPreviewAtlasPayload(sheet, coords);
    }
}
