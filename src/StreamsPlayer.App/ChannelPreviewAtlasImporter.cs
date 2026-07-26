using System.IO;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// Outcome of one atlas import. <paramref name="CodecUnavailable"/> is a normal result on a Windows
/// build without a WebP WIC codec, not a fault.
/// </summary>
internal sealed record ChannelPreviewImportResult(
    int Seeded,
    int Skipped,
    int OutOfBounds,
    bool CodecUnavailable)
{
    public static ChannelPreviewImportResult Unavailable { get; } = new(0, 0, 0, true);
}

/// <summary>
/// SP-0031 one-shot seeder: cuts the published channel-preview sheet into per-channel frames and writes
/// them into the existing preview store, so the grid shows real broadcast stills without connecting to
/// any stream.
/// </summary>
/// <remarks>
/// <para>
/// The sheet is decoded <em>once</em> and released when this returns. WPF/WIC has no region decoder -
/// unlike Android's <c>BitmapRegionDecoder</c>, the first pixel read of the 8160x7560 sheet materialises
/// the whole frame (measured: +265..491 MB working set, see <c>temp/SP-0031/RESEARCH.md</c> §3). Holding
/// it the way <see cref="FaviconTileLoader"/> holds the small favicon atlas is therefore impossible, and
/// per-tile lazy cropping would buy nothing. Paying it up front with <see cref="BitmapCacheOption.OnLoad"/>
/// makes the cost deterministic and every subsequent crop cheap.
/// </para>
/// <para>
/// <see cref="Import"/> is deliberately synchronous and must run on a single dedicated thread (the caller
/// wraps it in one <c>Task.Run</c>). A <c>CroppedBitmap</c> reaches back to the <c>BitmapDecoder</c> that
/// produced its source, and a decoder is thread-affine even when the frame itself is frozen: an await
/// between two crops lets the continuation resume on another pool thread and the next
/// <c>Freeze()</c> throws "the calling thread cannot access this object". Observed as a hard crash after
/// exactly one seeded tile before this was made single-threaded.
/// </para>
/// </remarks>
internal sealed class ChannelPreviewAtlasImporter
{
    // Progress is reported in whole tiles; batching keeps the marshaling cost off the hot loop.
    private const int ProgressInterval = 25;

    private readonly PreviewFrameStore _store;
    private readonly CurrentLog _log;

    public ChannelPreviewAtlasImporter(PreviewFrameStore store, CurrentLog log)
    {
        _store = store;
        _log = log;
    }

    public ChannelPreviewImportResult Import(
        ChannelPreviewAtlasPayload payload,
        IReadOnlyCollection<string> catalogUrls,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var sheet = TryDecodeSheet(payload.Sheet);
        if (sheet is null)
        {
            return ChannelPreviewImportResult.Unavailable;
        }

        var known = catalogUrls as IReadOnlySet<string> ?? new HashSet<string>(catalogUrls, StringComparer.Ordinal);
        var seeded = 0;
        var skipped = 0;
        var outOfBounds = 0;
        var absent = 0;
        var processed = 0;

        foreach (var (url, index) in payload.Coords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            if (processed % ProgressInterval == 0)
            {
                progress?.Report(seeded);
            }

            if (!known.Contains(url))
            {
                absent++; // the sheet covers the publisher's catalog, which may be ahead of this install
                continue;
            }

            // A frame this app captured from the live stream is fresher and sharper than a canned tile,
            // so a slot that already has one is never overwritten.
            if (_store.Exists(url))
            {
                skipped++;
                continue;
            }

            if (!ChannelPreviewAtlas.IsInBounds(index, sheet.PixelWidth, sheet.PixelHeight))
            {
                outOfBounds++; // a stale sidecar pointing past this sheet yields no tile, never a crash
                continue;
            }

            if (_store.Write(url, CropFrozen(sheet, index)))
            {
                seeded++;
            }
        }

        // One pass at the end: TrimOnce walks the whole directory, so per-frame trimming would be
        // quadratic across ~1900 writes.
        _store.TrimOnce(cancellationToken);
        progress?.Report(seeded);
        _log.Event("PREVIEW ATLAS",
            $"seeded={seeded}",
            $"skipped_existing={skipped}",
            $"not_in_catalog={absent}",
            $"out_of_bounds={outOfBounds}",
            $"sheet={sheet.PixelWidth}x{sheet.PixelHeight}",
            $"tiles={payload.Coords.Count}");
        return new ChannelPreviewImportResult(seeded, skipped, outOfBounds, CodecUnavailable: false);
    }

    /// <summary>
    /// Decodes the sheet, or returns null when this machine cannot read the format. WPF ships no WebP
    /// codec: it works only where the WebP WIC component is registered, which is not guaranteed across
    /// every supported Windows build, so "cannot decode" is an expected outcome to degrade on.
    /// </summary>
    private BitmapSource? TryDecodeSheet(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze(); // cropping and encoding happen on worker threads
            return frame;
        }
        catch (FileFormatException ex)
        {
            _log.Event("PREVIEW ATLAS", "decode=unsupported", $"err={ex.GetType().Name}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            _log.Event("PREVIEW ATLAS", "decode=unsupported", $"err={ex.GetType().Name}");
            return null;
        }
        catch (ArgumentException ex)
        {
            // A truncated or mispublished payload reads as an invalid image rather than a crash.
            _log.Event("PREVIEW ATLAS", "decode=invalid", $"err={ex.GetType().Name}");
            return null;
        }
    }

    private static BitmapSource CropFrozen(BitmapSource sheet, int index)
    {
        var rect = ChannelPreviewAtlas.RectFor(index);
        var tile = new CroppedBitmap(sheet, new System.Windows.Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height));
        tile.Freeze();
        return tile;
    }
}
