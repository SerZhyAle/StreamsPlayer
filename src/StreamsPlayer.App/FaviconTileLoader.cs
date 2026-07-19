using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StreamsPlayer.App;

public static class FaviconTileLoader
{
    private static string? _loadedPath;
    private static BitmapSource? _atlas;
    private static readonly Dictionary<int, ImageSource> Tiles = [];

    public static ImageSource? Load(string? atlasPath, int? index, int? maximumIndex)
    {
        if (atlasPath is null || index is not int tileIndex || tileIndex < 0 || tileIndex > maximumIndex || !File.Exists(atlasPath))
        {
            return null;
        }

        if (!atlasPath.Equals(_loadedPath, StringComparison.OrdinalIgnoreCase))
        {
            LoadAtlas(atlasPath);
        }

        if (_atlas is null)
        {
            return null;
        }

        if (Tiles.TryGetValue(tileIndex, out var cached))
        {
            return cached;
        }

        var x = tileIndex % 16 * 32;
        var y = tileIndex / 16 * 32;
        if (x + 32 > _atlas.PixelWidth || y + 32 > _atlas.PixelHeight)
        {
            return null;
        }

        var tile = new CroppedBitmap(_atlas, new System.Windows.Int32Rect(x, y, 32, 32));
        tile.Freeze();
        Tiles[tileIndex] = tile;
        return tile;
    }

    private static void LoadAtlas(string atlasPath)
    {
        _loadedPath = atlasPath;
        _atlas = null;
        Tiles.Clear();
        try
        {
            using var stream = new FileStream(atlasPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            _atlas = decoder.Frames[0];
            _atlas.Freeze();
        }
        catch
        {
            _atlas = null;
        }
    }
}
