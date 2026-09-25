using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StreamsPlayer.App;

/// <remarks>
/// Every member is called on the UI thread; only the decode itself runs on a worker, and its result is
/// published back on the UI thread by the await, so the dictionaries need no lock.
/// </remarks>
public static class FaviconTileLoader
{
    private const int TileSize = 32;
    private const int TilesPerRow = 16;

    // SP-0052: keyed by path rather than holding one atlas at a time - up to three sheets (downloaded
    // catalog, snapshot, imported bank) are alive at once, and a single-slot cache would reload them on
    // every alternating row. SP-0125: bounded by Retain to the sheets the state still names, so a
    // superseded ~25 MB decoded sheet leaves memory on the refresh that replaced it.
    private static readonly Dictionary<string, LoadedAtlas> Atlases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Decoding = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? _live;

    /// <summary>
    /// SP-0125: raised on the UI thread when a sheet finished decoding. Rows read while it was decoding got
    /// no icon and showed the monogram; they re-read when this fires.
    /// </summary>
    public static event Action? AtlasReady;

    public static ImageSource? Load(string? atlasPath, int? index, int? maximumIndex)
    {
        // SP-0067: no File.Exists here. It was one syscall per first-shown row - tens per viewport while
        // scrolling - to answer a question the decode below already answers, and caches: a missing file
        // opens as "no sheet" exactly once per path, and every later row for that path reads the cached
        // failure instead of asking the file system again.
        if (atlasPath is null || index is not int tileIndex || tileIndex < 0 || tileIndex > maximumIndex)
        {
            return null;
        }

        if (!Atlases.TryGetValue(atlasPath, out var atlas))
        {
            // SP-0125: decoding a sheet is tens of megabytes of pixels; on the UI thread it froze the first
            // paint and every refresh. The row shows its monogram until AtlasReady.
            _ = DecodeAsync(atlasPath);
            return null;
        }

        return atlas.Tile(tileIndex);
    }

    /// <summary>
    /// SP-0125: the sheets the current state names. Any other decoded sheet - and every crop over it - is
    /// dropped here, and a decode still running for one is discarded when it lands.
    /// </summary>
    public static void Retain(IEnumerable<string?> livePaths)
    {
        _live = new HashSet<string>(livePaths.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Atlases.Keys.Where(path => !_live.Contains(path)).ToList())
        {
            Atlases.Remove(path);
        }
    }

    private static async Task DecodeAsync(string atlasPath)
    {
        if (!Decoding.Add(atlasPath))
        {
            return;
        }

        LoadedAtlas atlas;
        try
        {
            atlas = await Task.Run(() => LoadedAtlas.Open(atlasPath));
        }
        finally
        {
            Decoding.Remove(atlasPath);
        }

        if (_live is not null && !_live.Contains(atlasPath))
        {
            return;
        }

        Atlases[atlasPath] = atlas;
        AtlasReady?.Invoke();
    }

    private sealed class LoadedAtlas
    {
        // SP-0069: the crops were kept for the life of the atlas, so a full scroll of the shipped bank
        // left one wrapper per distinct favicon index - about 19 000 - with no bound at all. The pixels
        // are shared with the frozen sheet, so the cost is the wrappers rather than image data, but an
        // unbounded map is what the ticket exists to remove. Wholesale clearing is cheap here precisely
        // because a crop decodes nothing, and the cap is set far above one viewport so scrolling never
        // re-crops what is on screen.
        private const int MaximumCachedTiles = 4096;

        private readonly BitmapSource? _sheet;
        private readonly Dictionary<int, ImageSource> _tiles = [];

        private LoadedAtlas(BitmapSource? sheet)
        {
            _sheet = sheet;
        }

        /// <summary>
        /// Runs on a worker thread. SP-0125: a <see cref="BitmapImage"/> loaded with
        /// <see cref="BitmapCacheOption.OnLoad"/> and frozen owns its pixels outright. A frame taken from a
        /// <see cref="BitmapDecoder"/> does not - even frozen it stays bound to the decoder's thread, and every
        /// crop taken on the UI thread threw, which the binding swallowed as "no icon".
        /// </summary>
        public static LoadedAtlas Open(string atlasPath)
        {
            try
            {
                using var stream = new FileStream(atlasPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var sheet = new BitmapImage();
                sheet.BeginInit();
                sheet.CacheOption = BitmapCacheOption.OnLoad;
                sheet.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                sheet.StreamSource = stream;
                sheet.EndInit();
                sheet.Freeze();
                return new LoadedAtlas(sheet);
            }
            catch
            {
                // A missing, corrupt or half-written atlas costs the icons of its own bank and nothing
                // else; the failure is cached as "no sheet" so it is neither re-decoded nor re-probed
                // once per row. FileNotFoundException lands here like any other open failure, which is
                // what let SP-0067 drop the File.Exists that used to precede this.
                return new LoadedAtlas(null);
            }
        }

        public ImageSource? Tile(int tileIndex)
        {
            if (_sheet is null)
            {
                return null;
            }

            if (_tiles.TryGetValue(tileIndex, out var cached))
            {
                return cached;
            }

            var x = tileIndex % TilesPerRow * TileSize;
            var y = tileIndex / TilesPerRow * TileSize;
            if (x + TileSize > _sheet.PixelWidth || y + TileSize > _sheet.PixelHeight)
            {
                return null;
            }

            var tile = new CroppedBitmap(_sheet, new System.Windows.Int32Rect(x, y, TileSize, TileSize));
            tile.Freeze();
            if (_tiles.Count >= MaximumCachedTiles)
            {
                _tiles.Clear();
            }

            _tiles[tileIndex] = tile;
            return tile;
        }
    }
}
