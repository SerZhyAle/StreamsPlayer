using System.Windows.Controls;
using System.Windows.Threading;

namespace StreamPlayer.App;

public partial class MainWindow
{
    private void RestoreBrowsingSession()
    {
        _restoringBrowsingSession = true;
        try
        {
            SearchBox.Text = _state.CatalogSearchQuery;
            SelectOptionValue(MediaFilter, _state.CatalogMediaFilter, AllValue);
            SelectOptionValue(CategoryFilter, _state.CatalogCategoryFilter, AllValue);
            SelectOptionValue(LanguageFilter, _state.CatalogLanguageFilter, AllValue);
            SelectOptionValue(CountryFilter, _state.CatalogCountryFilter, AllValue);
            SelectOptionValue(SortMode, _state.CatalogSortMode, "Name");
            _lastVisibleChannelId = _state.CatalogScrollAnchorId;
        }
        finally
        {
            _restoringBrowsingSession = false;
        }
    }

    private static void SelectOptionValue(ComboBox comboBox, string? value, string fallback)
    {
        var selected = value ?? fallback;
        comboBox.SelectedItem = comboBox.Items.OfType<UiOption>().FirstOrDefault(item =>
            item.Value.Equals(selected, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<UiOption>().FirstOrDefault(item => item.Value == fallback);
    }

    private async Task RestoreScrollAnchorAsync()
    {
        if (_lastVisibleChannelId is not Guid anchorId)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var row = GridRows.FirstOrDefault(candidate => candidate.Items.Any(item => item.Channel.Id == anchorId));
        if (row is not null)
        {
            StreamsList.ScrollIntoView(row);
        }
    }

    private void ScrollToCatalogStart()
    {
        _lastVisibleChannelId = null;
        if (GridRows.Count > 0)
        {
            StreamsList.ScrollIntoView(GridRows[0]);
        }
    }

    private Guid? GetFirstVisibleChannelId()
    {
        var viewport = new System.Windows.Rect(0, 0, StreamsList.ActualWidth, StreamsList.ActualHeight);
        for (var index = 0; index < GridRows.Count; index++)
        {
            if (StreamsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem container ||
                !container.IsVisible)
            {
                continue;
            }

            var bounds = container.TransformToAncestor(StreamsList)
                .TransformBounds(new System.Windows.Rect(container.RenderSize));
            if (bounds.IntersectsWith(viewport))
            {
                return GridRows[index].Items.FirstOrDefault()?.Channel.Id;
            }
        }

        return null;
    }

    private void ScheduleBrowsingSessionSave()
    {
        if (!_preferencesLoaded || _restoringBrowsingSession)
        {
            return;
        }

        _browsingSessionSaveTimer.Stop();
        _browsingSessionSaveTimer.Start();
    }

    private async void BrowsingSessionSaveTimer_Tick(object? sender, EventArgs e)
    {
        _browsingSessionSaveTimer.Stop();
        await SaveBrowsingSessionAsync();
    }

    private async Task SaveBrowsingSessionAsync()
    {
        if (!_preferencesLoaded)
        {
            return;
        }

        _state = await _store.SaveAsync(_state with
        {
            CatalogSearchQuery = SearchBox.Text,
            CatalogMediaFilter = SelectedOptionValue(MediaFilter) ?? AllValue,
            CatalogCategoryFilter = SelectedOptionValue(CategoryFilter) ?? AllValue,
            CatalogLanguageFilter = SelectedOptionValue(LanguageFilter) ?? AllValue,
            CatalogCountryFilter = SelectedOptionValue(CountryFilter) ?? AllValue,
            CatalogSortMode = SelectedOptionValue(SortMode) ?? "Name",
            CatalogScrollAnchorId = _lastVisibleChannelId
        });
    }
}
