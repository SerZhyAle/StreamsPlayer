using System.Windows;
using System.Windows.Controls;

namespace StreamsPlayer.App;

// SP-0050: the filter and sorting row is mounted only when the user asks for it. Nothing here touches a
// facet value or the search text - revealing and hiding is purely about the row's presence.
public partial class MainWindow
{
    private const string FiltersActiveTag = "Active";

    /// <summary>
    /// How many facets are narrowing the catalog right now. <c>SortMode</c> is excluded on purpose: it
    /// reorders, it never narrows, and <c>ClearFiltersButton_Click</c> already leaves it alone - counting
    /// it would mark the reveal button for a user who merely re-sorted, the exact false alarm the mark
    /// exists to avoid.
    /// </summary>
    private int ActiveFacetCount()
    {
        ComboBox[] facets = [MediaFilter, CategoryFilter, TopicFilter, LanguageFilter, CountryFilter, MinBitrateFilter, CollectionFilter];
        return facets.Count(facet =>
            !string.Equals(SelectedOptionValue(facet) ?? AllValue, AllValue, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateFilterPanelChrome()
    {
        var visible = _state.CatalogFiltersVisible;
        FilterPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FiltersButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        // The mark only means something while the row is hidden: with the row open the facets speak for
        // themselves.
        var active = visible ? 0 : ActiveFacetCount();
        FiltersButton.Tag = active > 0 ? FiltersActiveTag : null;
        FiltersButton.ToolTip = active > 0
            ? LocalizationService.Format("FiltersActiveTip", active)
            : LocalizationService.Get("FiltersAndSortingTip");
    }

    /// <summary>
    /// Writes straight through <see cref="PersistAsync"/> rather than the debounced browsing-session
    /// timer: this is a deliberate, rare click, and the debounce exists for keystrokes and scrolling.
    /// </summary>
    private async Task SetFilterPanelVisibleAsync(bool visible)
    {
        _state = await PersistAsync(_state with { CatalogFiltersVisible = visible });
        UpdateFilterPanelChrome();
    }

    private async void FiltersButton_Click(object sender, RoutedEventArgs e) => await SetFilterPanelVisibleAsync(true);

    private async void HideFiltersButton_Click(object sender, RoutedEventArgs e) => await SetFilterPanelVisibleAsync(false);
}
