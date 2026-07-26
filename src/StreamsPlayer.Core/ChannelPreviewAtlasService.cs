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

    // The sheet is ~11 MB today and may grow toward the publisher's 30 MiB ceiling, so this bound is
    // sized for a slow link - it is not the catalog's 30 s deadline.
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;

    public ChannelPreviewAtlasService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ChannelPreviewAtlasPayload> DownloadAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        // Sidecar first: at ~135 KB it is the cheap half, so a broken or missing publish fails before
        // the multi-megabyte sheet is pulled.
        using var coordsResponse = await _httpClient.GetAsync(CoordsUrl, deadline.Token);
        coordsResponse.EnsureSuccessStatusCode();
        var coordsJson = await coordsResponse.Content.ReadAsStringAsync(deadline.Token);
        var coords = ChannelPreviewCoords.Parse(coordsJson);
        if (coords.Count == 0)
        {
            throw new InvalidDataException("The channel-preview sidecar lists no tiles.");
        }

        using var sheetResponse = await _httpClient.GetAsync(AtlasUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        sheetResponse.EnsureSuccessStatusCode();
        if (sheetResponse.Content.Headers.ContentLength > MaximumSheetBytes)
        {
            throw new InvalidDataException(
                $"The channel-preview atlas is larger than the {MaximumSheetBytes} byte ceiling.");
        }

        var sheet = await sheetResponse.Content.ReadAsByteArrayAsync(deadline.Token);
        if (sheet.Length > MaximumSheetBytes)
        {
            throw new InvalidDataException(
                $"The channel-preview atlas is larger than the {MaximumSheetBytes} byte ceiling.");
        }

        return new ChannelPreviewAtlasPayload(sheet, coords);
    }
}
