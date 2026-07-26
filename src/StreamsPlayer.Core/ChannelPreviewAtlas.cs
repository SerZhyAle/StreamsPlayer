namespace StreamsPlayer.Core;

/// <summary>The pixel rectangle of one tile inside the channel-preview sheet.</summary>
public readonly record struct PreviewTileRect(int Left, int Top, int Width, int Height);

/// <summary>
/// SP-0031 consumer half of the published channel-preview sprite contract. The offline packer
/// (FastMediaSorter <c>delivery/stream-catalog/README.md</c>, "Channel preview atlas") emits tile
/// indices against exactly this geometry - changing one side without the other drifts every rect,
/// so these three constants are the shared invariant, not tunables.
/// </summary>
public static class ChannelPreviewAtlas
{
    public const int TileWidth = 240;
    public const int TileHeight = 135;
    public const int Columns = 34;

    /// <summary>The rect of tile <paramref name="index"/>: <c>col = index % Columns</c>, <c>row = index / Columns</c>.</summary>
    public static PreviewTileRect RectFor(int index) =>
        new(index % Columns * TileWidth, index / Columns * TileHeight, TileWidth, TileHeight);

    /// <summary>
    /// True only when the index is non-negative and its rect fits the given sheet. A stale sidecar
    /// pointing past a shrunk sheet must yield no tile rather than a decode error, so callers test
    /// against the sheet they actually decoded, never against the documented maximum.
    /// </summary>
    public static bool IsInBounds(int index, int sheetWidth, int sheetHeight)
    {
        if (index < 0)
        {
            return false;
        }

        var rect = RectFor(index);
        return rect.Left + rect.Width <= sheetWidth && rect.Top + rect.Height <= sheetHeight;
    }
}
