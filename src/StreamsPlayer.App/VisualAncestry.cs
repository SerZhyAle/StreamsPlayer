using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace StreamsPlayer.App;

/// <summary>Questions about where an input event's original source sits in the tree.</summary>
internal static class VisualAncestry
{
    /// <summary>
    /// Whether <paramref name="source"/> is inside a button that lies below <paramref name="boundary"/>.
    /// </summary>
    /// <remarks>
    /// SP-0132: a double-click is two clicks, and a button inside a card has already acted on both. A card
    /// that also plays on the double-click turned two presses on Play into several player windows, two on
    /// Pin into a pin and a play, and on radio into play-stop-play that cancelled the sleep timer.
    /// </remarks>
    public static bool IsInsideButton(DependencyObject? source, DependencyObject boundary)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, boundary); node = ParentOf(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    // A text run under the pointer is a ContentElement, not a Visual, and VisualTreeHelper.GetParent throws
    // on one - the logical parent is the way back up from there.
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
