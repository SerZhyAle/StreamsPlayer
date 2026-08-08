using System.Windows.Threading;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0058: put one channel in front of the user - select it and bring it on screen, clearing whatever
/// is currently hiding it.
/// </summary>
/// <remarks>
/// Its own file because none of the obstacles is specific to sharing: <c>StreamsList</c> is a virtualized
/// list whose items are <see cref="CatalogGridRow"/> chunks rather than channels, a pinned channel is in
/// <c>PinnedRows</c> and not in <c>Rows</c> at all, and a live search or facet can exclude the target
/// outright.
/// </remarks>
public partial class MainWindow
{
    private bool IsChannelVisible(Guid channelId) =>
        Rows.Any(row => row.Channel.Id == channelId) ||
        PinnedRows.Any(row => row.Channel.Id == channelId);

    /// <summary>
    /// Clears the search text and every facet when the channel is not currently shown. Returns true only
    /// when something was actually cleared, so the caller can tell the user why their search box emptied.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through <c>FilterChanged</c> or <c>ApplyFilterNow</c>: both end in
    /// <c>ScrollToCatalogStart</c>, which would undo the scroll this exists to perform. The
    /// <c>_resettingFilters</c> guard is what stops the clear firing an evaluation per control, exactly
    /// as <c>ClearFiltersButton_Click</c> uses it; the pending debounce is stopped for the same reason -
    /// a timer left running from earlier typing would re-scroll to the top a moment later.
    /// </remarks>
    private bool ClearWhatHidesTheRow(Guid channelId)
    {
        if (IsChannelVisible(channelId))
        {
            return false;
        }

        _resettingFilters = true;
        try
        {
            SearchBox.Clear();
            SelectOptionValue(MediaFilter, AllValue, AllValue);
            SelectOptionValue(CategoryFilter, AllValue, AllValue);
            SelectOptionValue(TopicFilter, AllValue, AllValue);
            SelectOptionValue(LanguageFilter, AllValue, AllValue);
            SelectOptionValue(CountryFilter, AllValue, AllValue);
            SelectOptionValue(MinBitrateFilter, AllValue, AllValue);
            SelectOptionValue(CollectionFilter, AllValue, AllValue);
        }
        finally
        {
            _resettingFilters = false;
        }

        _filterDebounce?.Stop();
        ApplyFilter();
        UpdateFilterPanelChrome();
        ScheduleBrowsingSessionSave();
        return true;
    }

    /// <summary>Selects a channel, scrolls it into view, and says so when a filter had to be dropped.</summary>
    /// <remarks>A pinned channel yields no grid row and needs no scroll: the pinned band sits at the top.</remarks>
    private async Task RevealChannelAsync(Guid channelId)
    {
        var cleared = ClearWhatHidesTheRow(channelId);
        if (_rowCache.TryGetValue(channelId, out var row))
        {
            SelectRow(row);
        }

        // The containers are recycled and virtualized, so the grid row that holds this channel does not
        // exist until layout has run. RestoreScrollAnchorAsync pumps the dispatcher for the same reason.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var gridRow = GridRows.FirstOrDefault(candidate => candidate.Items.Any(item => item.Channel.Id == channelId));
        if (gridRow is not null)
        {
            StreamsList.ScrollIntoView(gridRow);
        }

        if (cleared && row is not null)
        {
            SetStatus("RevealClearedFilters", StreamTitleFormatter.Display(row.Channel.Title));
        }
    }
}
