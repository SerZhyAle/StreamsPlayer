using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0087: the colours a channel's monogram plate can take when the bank gave it no icon.
/// </summary>
/// <remarks>
/// Twelve mid-tone, deliberately desaturated colours. Desaturated because a saturated set of twelve
/// reads as a toy next to real station logos; mid-tone because the same plate has to hold white text
/// against both the light card (<c>#FFF8FAFC</c>) and the dark one (<c>#FF1A2535</c>) - there is one
/// plate colour per channel, not one per theme, so the channel keeps its identity when the theme
/// changes.
/// <para>
/// Every brush is frozen and shared. One brush per realized card would be an allocation on every
/// scrolled row over a catalog of 19 534 - the same reason <c>TileScrimBrush</c> in
/// <c>MainWindow.xaml</c> is a single frozen resource.
/// </para>
/// </remarks>
internal static class MonogramPalette
{
    private static readonly Brush[] Plates = Freeze(
        "#FF3B6FB5", "#FF2F8F6E", "#FF8A5A9E", "#FFB5643C",
        "#FF4A7BA7", "#FF7A8C3C", "#FFA5504F", "#FF3F7F8F",
        "#FF6B5EA8", "#FF9A7431", "#FF4E8C55", "#FF8B5570");

    /// <summary>How many distinct plates exist. Read from the table so it can never disagree with it.</summary>
    internal static int Size => Plates.Length;

    /// <summary>The plate this channel owns, the same one on every launch and every machine.</summary>
    internal static Brush Plate(string? title) => Plates[ChannelMonogram.PaletteIndex(title, Size)];

    /// <summary>The text colour every plate is chosen to carry.</summary>
    internal static Brush Foreground { get; } = FreezeOne(Colors.White);

    private static Brush[] Freeze(params string[] colours) =>
        [.. colours.Select(colour => FreezeOne((Color)ColorConverter.ConvertFromString(colour)!))];

    private static Brush FreezeOne(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
