using System.IO;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// Outcome of one artwork import. <paramref name="CodecUnavailable"/> is a normal result on a Windows
/// build without a WebP WIC codec, not a fault.
/// </summary>
internal sealed record ChannelPreviewImportResult(
    int Seeded,
    int Skipped,
    int Missing,
    bool CodecUnavailable)
{
    public static ChannelPreviewImportResult Unavailable { get; } = new(0, 0, 0, true);
}

/// <summary>
/// SP-0031 one-shot seeder, moved onto the tile pack by SP-0091: unpacks the published per-channel
/// stills into the existing preview store, so the grid shows real broadcast frames without connecting
/// to any stream.
/// </summary>
/// <remarks>
/// <para>
/// This used to cut tiles out of a sprite sheet, and the sheet is what made it expensive and fragile.
/// WPF/WIC has no region decoder - unlike Android's <c>BitmapRegionDecoder</c>, the first pixel read
/// materialised the whole frame (measured: +265..491 MB working set on the 61,7 Mpx sheet, see
/// <c>temp/SP-0031/RESEARCH.md</c> §3), so the entire sheet had to be decoded up front even to seed two
/// channels, and every crop had to stay on the decoder's thread. The pack costs one small decode per
/// tile actually written and nothing at all for the ones skipped, which on a re-import is nearly all of
/// them.
/// </para>
/// <para>
/// Still deliberately synchronous on one thread, for a different reason: a <see cref="ChannelPreviewTilePack"/>
/// reads its entries through one shared <c>ZipArchive</c> stream. The frames it produces are fully
/// decoded and frozen, so they travel between threads freely - unlike the <c>CroppedBitmap</c>s of the
/// sheet path, which reached back into a thread-affine decoder and crashed after exactly one seeded tile
/// when this ran on the pool.
/// </para>
/// </remarks>
internal sealed class ChannelPreviewImporter
{
    // Progress is reported in whole tiles; batching keeps the marshaling cost off the hot loop.
    private const int ProgressInterval = 25;

    /// <summary>
    /// Largest decoded tile accepted, in pixels: the published 240 x 135 geometry with the same x16 slack
    /// <see cref="ChannelPreviewTilePack.MaximumTileBytes"/> allows, so a legitimate re-tiling still fits.
    /// </summary>
    private const long MaximumTilePixels = 240L * 135 * 16;

    private readonly PreviewFrameStore _store;
    private readonly CurrentLog _log;

    public ChannelPreviewImporter(PreviewFrameStore store, CurrentLog log)
    {
        _store = store;
        _log = log;
    }

    /// <param name="progress">
    /// Reports tiles examined against the sidecar's total. It deliberately does not report tiles seeded:
    /// a tile skipped because its channel is absent from this install or because a captured frame already
    /// exists is real work that took real time, so counting only writes left the number frozen for
    /// minutes on a re-import where almost everything is skipped.
    /// </param>
    public ChannelPreviewImportResult Import(
        ChannelPreviewArtwork artwork,
        IReadOnlyCollection<string> catalogUrls,
        IProgress<(int Processed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        using var pack = ChannelPreviewTilePack.Open(artwork.TilePack);
        if (!CanDecodeTiles(pack))
        {
            return ChannelPreviewImportResult.Unavailable;
        }

        var known = catalogUrls as IReadOnlySet<string> ?? new HashSet<string>(catalogUrls, StringComparer.Ordinal);
        var total = artwork.Coords.Count;
        var seeded = 0;
        var skipped = 0;
        var missing = 0;
        var absent = 0;
        var undecodable = 0;
        var processed = 0;

        foreach (var (url, index) in artwork.Coords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            if (processed % ProgressInterval == 0)
            {
                progress?.Report((processed, total));
            }

            if (!known.Contains(url))
            {
                absent++; // the pack covers the publisher's catalog, which may be ahead of this install
                continue;
            }

            // A frame this app captured from the live stream is fresher and sharper than a canned tile,
            // so a slot that already has one is never overwritten. Checked before the tile is read, which
            // is what makes a re-import cheap.
            if (_store.Exists(url))
            {
                skipped++;
                continue;
            }

            var tile = pack.Read(index);
            if (tile is null)
            {
                missing++; // a sidecar slot the pack has no capture for; a gap, not a fault
                continue;
            }

            var frame = TryDecodeTile(tile);
            if (frame is null)
            {
                undecodable++;
                continue;
            }

            if (_store.Write(url, frame))
            {
                seeded++;
            }
        }

        // One pass at the end: TrimOnce walks the whole directory, so per-frame trimming would be
        // quadratic across ~2000 writes.
        _store.TrimOnce(cancellationToken);
        // After the trim, not before it: the directory walk is the last measurable step, and a bar that
        // reached full and then sat through it would be the same lie in miniature.
        progress?.Report((total, total));
        _log.Event("PREVIEW ARTWORK",
            $"seeded={seeded}",
            $"skipped_existing={skipped}",
            $"not_in_catalog={absent}",
            $"no_tile={missing}",
            $"undecodable={undecodable}",
            $"pack_tiles={pack.Count}",
            $"coords={artwork.Coords.Count}",
            $"stamp={artwork.Stamp}");
        return new ChannelPreviewImportResult(seeded, skipped, missing, CodecUnavailable: false);
    }

    /// <summary>
    /// Decodes one tile up front to find out whether this machine can read the format at all. WPF ships
    /// no WebP codec: it works only where the WebP WIC component is registered, which is not guaranteed
    /// across every supported Windows build, so "cannot decode" is an expected outcome to degrade on.
    /// </summary>
    /// <remarks>
    /// The probe reads the first slot only. The pack has passed the structural checks (it opens, every
    /// slot name is a decimal, the per-tile ceiling held) and the publisher's tiles are all built by one
    /// tool in one format, so a decode failure is almost always this machine's missing WebP codec rather
    /// than the file. Since SP-0160 (item L) the manifest hashes no longer prove the bytes, so a corrupt
    /// first tile would be misread as "cannot decode" - accepted as the cost of not probing thousands of
    /// failed decodes on a codec-less machine. An empty pack is not "incapable" - it is a bad publish,
    /// and the loop reports it as zero seeded.
    /// </remarks>
    private bool CanDecodeTiles(ChannelPreviewTilePack pack)
    {
        foreach (var slot in pack.Slots)
        {
            var tile = pack.Read(slot);
            if (tile is null)
            {
                continue;
            }

            if (TryDecodeTile(tile) is not null)
            {
                return true;
            }

            _log.Event("PREVIEW ARTWORK", "decode=unsupported", $"probe_slot={slot}");
            return false;
        }

        // No readable slot at all, which is a bad publish rather than a codec verdict: report it as an
        // import that seeded nothing, not as "this computer cannot open the pack".
        _log.Event("PREVIEW ARTWORK", "pack=empty");
        return true;
    }

    /// <summary>
    /// Decodes one tile, or returns null when it cannot be read. Silent on purpose: the caller either
    /// probes once and reports the verdict, or counts failures across thousands of tiles, and a log line
    /// per tile would bury the run.
    /// </summary>
    /// <remarks>
    /// S8-3: the pack bounds a tile's compressed size, not what it inflates to, so a few hundred bytes of
    /// WebP can declare a canvas of gigapixels. The header is read first (<see cref="BitmapCacheOption.None"/>
    /// decodes nothing yet), a tile over <see cref="MaximumTilePixels"/> is refused, and only then are the
    /// pixels pulled into memory.
    /// </remarks>
    private static BitmapSource? TryDecodeTile(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                (long)frame.PixelWidth * frame.PixelHeight > MaximumTilePixels)
            {
                return null;
            }

            // The copy completes inside the constructor, while the stream above is still open.
            var loaded = new CachedBitmap(frame, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            loaded.Freeze(); // OnLoad + Freeze is what lets the encode happen off this thread later
            return loaded;
        }
        // A truncated or mispublished tile reads as an invalid image rather than a crash. WIC reports its
        // failures as a mix of managed and COM exceptions, so the whole decode set is named here: the
        // invalid-operation case is a missing or unregistered codec, the COM/IO/overflow cases a corrupt
        // stream or a canvas the allocator refuses. Anything outside this set is a real fault and propagates.
        catch (Exception exception) when (exception is FileFormatException or NotSupportedException
            or ArgumentException or InvalidOperationException or IOException or OverflowException
            or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
