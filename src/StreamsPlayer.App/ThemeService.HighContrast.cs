using System.Windows;
using System.Windows.Media;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0211: the high-contrast palette. While Windows is in a high-contrast theme every palette role resolves
/// to the user's own system colours, whatever theme the user chose in the application
/// (<c>APP-SETTINGS</c> rule 6, <c>WINDOWS-UI</c> section 6.5). Nothing is re-authored: each role maps to the
/// system colour the platform guarantees to read against the one it is drawn on.
/// </summary>
public static partial class ThemeService
{
    /// <summary>
    /// The pairs that matter: text is <c>WindowText</c> on <c>Window</c> or <c>ButtonText</c> on
    /// <c>ButtonFace</c>; a filled accent is <c>Highlight</c> carrying <c>HighlightText</c>; a mark or a link
    /// drawn straight on the window is <c>HotTrack</c>; the unavailable ink is <c>GrayText</c>. Decorative
    /// identity inks and state hues collapse to the text colour - the caption and the shape say the rest
    /// (<c>WINDOWS-UI</c> 6.5, 6.6). <c>FaviconPlateBrush</c> has no entry on purpose: it is the plate under a
    /// station's own artwork, content rather than chrome, so it keeps its table value.
    /// </summary>
    private static Dictionary<string, Color> HighContrastColours()
    {
        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var face = SystemColors.ControlColor;
        var highlight = SystemColors.HighlightColor;
        var highlightText = SystemColors.HighlightTextColor;
        var hotTrack = SystemColors.HotTrackColor;
        var gray = SystemColors.GrayTextColor;

        return new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["WindowBackground"] = window,
            ["SurfaceBrush"] = window,
            ["TextBrush"] = text,
            ["MutedBrush"] = text,
            ["AccentBrush"] = highlight,
            ["AccentTextBrush"] = highlightText,
            ["AccentMarkBrush"] = hotTrack,
            ["SelectionBrush"] = highlight,
            ["BorderBrush"] = text,
            ["ControlBrush"] = face,
            ["ControlHoverBrush"] = face,
            ["ControlPressedBrush"] = face,
            ["CardBrush"] = window,
            ["CardSelectedBrush"] = window,
            ["SectionBrush"] = window,
            ["DisabledTextBrush"] = gray,
            ["InfoBrush"] = text,
            ["GeoHintBrush"] = window,
            ["GeoTextBrush"] = text,
            ["LinkBrush"] = hotTrack,
            ["SuccessBrush"] = text,
            ["WarningBrush"] = text,
            ["DangerBrush"] = text,
            ["NavInkGeneral"] = text,
            ["NavInkLibrary"] = text,
            ["NavInkPlayback"] = text,
            ["NavInkAudio"] = text,
            ["NavInkFiles"] = text,
            ["NavInkAbout"] = text
        };
    }
}
