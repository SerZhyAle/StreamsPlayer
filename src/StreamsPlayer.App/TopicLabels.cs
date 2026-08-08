using System.Globalization;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0061: the one place a catalog rubric becomes a word, and the one order rubrics are shown in.
/// </summary>
/// <remarks>
/// Nothing here is cached. The dictionary is swapped at runtime when the interface language changes,
/// and every consumer - the facet, the list row, the About window - already re-renders on that swap.
/// A cache would be a second copy that has to be told.
/// </remarks>
internal static class TopicLabels
{
    /// <summary>
    /// The rubric as the reader sees it: a translated label for one of the bank's rubrics, and the
    /// identifier itself for anything else - a catalog newer than this build, or a hand-typed rubric on
    /// a manually added channel. An unknown value is shown, never dropped and never replaced.
    /// </summary>
    public static string Text(string? topic) =>
        CatalogTopics.ResourceKey(topic) is { } key
            ? LocalizationService.Get(key)
            : topic?.Trim() ?? string.Empty;

    /// <summary>
    /// Orders rubrics by their <b>label</b> under the interface culture, with
    /// <see cref="CatalogTopics.General"/> last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordering by the identifier is the defect this replaces: an ordinal sort over English words puts
    /// the Russian or Arabic list in what reads as random order, because the key the reader can see is
    /// not the key being compared. <see cref="CultureInfo.CurrentUICulture"/> is what
    /// <see cref="LocalizationService.Apply"/> sets, so the collation follows the chosen language.
    /// </para>
    /// <para>
    /// General is last because it is 48.2% of the bank - a pick that narrows almost nothing would
    /// otherwise sit in the middle of the alphabet among rubrics that narrow a great deal, with nothing
    /// to say so. It stays selectable: hiding it would make half the catalog unreachable through the
    /// facet while those channels still name it in their row.
    /// </para>
    /// </remarks>
    public static IComparer<string> Comparer { get; } = new LabelComparer();

    private sealed class LabelComparer : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            var leftIsGeneral = IsGeneral(left);
            var rightIsGeneral = IsGeneral(right);
            if (leftIsGeneral != rightIsGeneral)
            {
                return leftIsGeneral ? 1 : -1;
            }

            return StringComparer
                .Create(CultureInfo.CurrentUICulture, ignoreCase: true)
                .Compare(Text(left), Text(right));
        }

        private static bool IsGeneral(string? topic) =>
            string.Equals(topic?.Trim(), CatalogTopics.General, StringComparison.OrdinalIgnoreCase);
    }
}
