using System.Net.Http;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// The downloaded channel-preview artwork: the tile pack bytes, its <c>url -&gt; slot</c> map, and the
/// manifest stamp identifying the build both came from.
/// </summary>
public sealed record ChannelPreviewArtwork(
    string Stamp,
    DateTimeOffset? GeneratedAt,
    IReadOnlyDictionary<string, int> Coords,
    byte[] TilePack);

/// <summary>
/// SP-0031 downloader for the published channel-preview artwork, rewritten for SP-0091 against source
/// contract items F and G2: stable names, manifest-verified, tile pack rather than sprite sheet.
/// </summary>
/// <remarks>
/// <para>These are release assets with their own lifecycle, deliberately not bundled into
/// <c>stream-catalog.zip</c> because they are an order of magnitude larger than the CSV. Called only
/// from an explicitly accepted user offer; there is no automatic or background fetch, and this class
/// touches the network for nothing else - the manifest is fetched inside an accepted download, not to
/// decide whether to offer.</para>
/// <para>Order is deliberate: manifest (1 KB), sidecar (200 KB), pack (15 MB). Each step is cheaper than
/// the next and can refuse the whole thing, so a broken or half-replaced publish fails before the
/// multi-megabyte transfer rather than after it.</para>
/// </remarks>
public sealed class ChannelPreviewArtworkService
{
    private const string ReleaseUrl =
        "https://github.com/SerZhyAle/FastMediaSorter_mob_v2/releases/download/delivery-so-v1";

    /// <summary>
    /// The invalidation handle. Read this, never a revisioned asset name: revisioned names are frozen
    /// artifacts that are never rebuilt again, so pinning one holds a payload that has stopped being
    /// maintained while answering 200 forever.
    /// </summary>
    public const string ManifestUrl = $"{ReleaseUrl}/artwork-manifest.json";

    public const string CoordsFile = "channel-preview-coords.json";
    public const string TilePackFile = "channel-preview-tiles.zip";

    public const string CoordsUrl = $"{ReleaseUrl}/{CoordsFile}";
    public const string TilePackUrl = $"{ReleaseUrl}/{TilePackFile}";

    /// <summary>
    /// Hard ceiling on the pack held in memory. Read it as a ceiling, never as a size: the pack is
    /// republished without an app release and grows with the video-channel count (14,6 MB on
    /// 2026-08-20). What it rules out is the mis-published or hostile response, not a larger catalog.
    /// </summary>
    public const long MaximumTilePackBytes = 48L * 1024 * 1024;

    /// <summary>Ceilings for the two small files, on the same "refuse the absurd" footing.</summary>
    public const long MaximumManifestBytes = 1024L * 1024;

    public const long MaximumCoordsBytes = 16L * 1024 * 1024;

    // SP-0056: the small files are 1 KB and 200 KB. If either has not arrived in this long, the publish
    // or the link is broken, and failing on the cheap half before the multi-megabyte pack is the point of
    // fetching them first.
    private static readonly TimeSpan SidecarDeadline = TimeSpan.FromSeconds(30);

    // SP-0056: the pack gets no total-duration bound at all - this is it. A wall-clock limit "sized for a
    // slow link" is the job a silence bound does properly: a large asset over a slow line finishes
    // however long it takes, while a socket that stops answering fails in twenty seconds instead of after
    // minutes the user cannot tell from a hang.
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;

    public ChannelPreviewArtworkService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ChannelPreviewArtwork> DownloadAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = ArtworkManifest.Parse(
            Encoding.UTF8.GetString(await GetSmallAsync(ManifestUrl, MaximumManifestBytes, cancellationToken)));
        var set = manifest.Set(ArtworkManifest.ChannelPreviewSet);

        // Verified against the manifest before it is parsed: an index map from a different build resolves
        // every URL to somebody else's picture, and nothing downstream would ever notice.
        var coordsBytes = await GetSmallAsync(CoordsUrl, MaximumCoordsBytes, cancellationToken);
        set.File(CoordsFile).Verify(coordsBytes);
        var coords = ChannelPreviewCoords.Parse(Encoding.UTF8.GetString(coordsBytes));
        if (coords.Count == 0)
        {
            throw new InvalidDataException("The channel-preview sidecar lists no tiles.");
        }

        using var packResponse = await _httpClient.GetAsync(
            TilePackUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        packResponse.EnsureSuccessStatusCode();
        // The ceiling is checked against the declared length and against the bytes that actually arrive;
        // both live in the download loop, so doing either here would leave one condition with two
        // messages.
        var pack = await HttpDownload.ReadAllBytesAsync(
            packResponse, progress, MaximumTilePackBytes, DownloadIdleTimeout, cancellationToken);
        set.File(TilePackFile).Verify(pack);

        return new ChannelPreviewArtwork(set.Stamp, manifest.GeneratedAt, coords, pack);
    }

    /// <summary>
    /// Fetches one of the two small files under a total deadline. They report no progress by design - a
    /// fetch that completes in well under a second would flick a bar to full and back before the real
    /// download even starts.
    /// </summary>
    private async Task<byte[]> GetSmallAsync(string url, long ceilingBytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SidecarDeadline);
        using var response = await _httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        return await HttpDownload.ReadAllBytesAsync(
            response, progress: null, ceilingBytes, DownloadIdleTimeout, deadline.Token);
    }
}
