using System.Windows;
using System.Windows.Controls;

namespace StreamsPlayer.App;

// SP-0050: the filter and sorting row is mounted only when the user asks for it. Nothing here touches a
// facet value or the search text - revealing and hiding is purely about the row's presence.
public partial class MainWindow
{
    private const string FiltersActiveTag = "Active";

    // SP-0094: the Media facet's two narrowing values, named once. They are data - the facet stores
    // them and ApplyFilter compares against them - so they must never be replaced by a label.
    private const string AudioFilterValue = "Audio";
    private const string VideoFilterValue = "Video";

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
        UpdateQuickMediaChrome();
    }

    /// <summary>
    /// SP-0094: paints the two quick buttons from the Media facet, which is the single value both forms
    /// show. Called from <see cref="UpdateFilterPanelChrome"/> rather than from the click handlers, so
    /// every path that can move the facet - the dropdown, Clear, the reveal, the session restore, a
    /// language switch - repaints them without knowing they exist.
    /// </summary>
    private void UpdateQuickMediaChrome()
    {
        var media = SelectedOptionValue(MediaFilter) ?? AllValue;
        QuickVideoButton.Tag = media == VideoFilterValue ? FiltersActiveTag : null;
        QuickAudioButton.Tag = media == AudioFilterValue ? FiltersActiveTag : null;
    }

    /// <summary>
    /// Moves the Media facet to <paramref name="value"/>, or back to All when it is already there.
    /// </summary>
    /// <remarks>
    /// The <c>_resettingFilters</c> guard is the one <c>ClearFiltersButton_Click</c> uses, and for the
    /// same reason: writing the facet raises its SelectionChanged, and the debounce that handler starts
    /// would delay a button press by 200 ms for no gain. A click is a completed intention, so the pending
    /// timer is stopped and the evaluation runs now. <c>ApplyFilterNow</c> carries the repaint and the
    /// session save, so nothing here has to remember either.
    /// </remarks>
    private void ToggleQuickMediaFilter(string value)
    {
        var next = (SelectedOptionValue(MediaFilter) ?? AllValue) == value ? AllValue : value;
        _resettingFilters = true;
        try
        {
            SelectOptionValue(MediaFilter, next, AllValue);
        }
        finally
        {
            _resettingFilters = false;
        }

        _filterDebounce?.Stop();
        ApplyFilterNow();
    }

    private void QuickVideoButton_Click(object sender, RoutedEventArgs e) => ToggleQuickMediaFilter(VideoFilterValue);

    private void QuickAudioButton_Click(object sender, RoutedEventArgs e) => ToggleQuickMediaFilter(AudioFilterValue);

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
