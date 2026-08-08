using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StreamsPlayer.App;

public static class FaviconTileLoader
{
    private const int TileSize = 32;
    private const int TilesPerRow = 16;

    // SP-0052: keyed by path rather than holding one atlas at a time. Two atlases - the downloaded one
    // and the bundled snapshot's - are alive at once as soon as a snapshot has been applied, and a
    // single-slot cache would reload both on every alternating row.
    private static readonly Dictionary<string, LoadedAtlas> Atlases = new(StringComparer.OrdinalIgnoreCase);

    // At most two atlases are ever in play, but each refresh writes a new file name, so a long session
    // would otherwise accumulate every atlas it ever saw - decoded sheets, several megabytes each.
    // Clearing wholesale on the third distinct path costs one reload of the two live ones and bounds the
    // cache without tracking which paths the state still names.
    private const int MaximumCachedAtlases = 2;

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
            if (Atlases.Count >= MaximumCachedAtlases)
            {
                Atlases.Clear();
            }

            atlas = LoadedAtlas.Open(atlasPath);
            Atlases[atlasPath] = atlas;
        }

        return atlas.Tile(tileIndex);
    }

    private sealed class LoadedAtlas
    {
        private readonly BitmapSource? _sheet;
        private readonly Dictionary<int, ImageSource> _tiles = [];

        private LoadedAtlas(BitmapSource? sheet)
        {
            _sheet = sheet;
        }

        public static LoadedAtlas Open(string atlasPath)
        {
            try
            {
                using var stream = new FileStream(atlasPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var sheet = decoder.Frames[0];
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
            _tiles[tileIndex] = tile;
            return tile;
        }
    }
}
