using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StreamPlayer.Core;

namespace StreamPlayer.App;

public partial class MainWindow
{
    private async void ListModeButton_Click(object sender, RoutedEventArgs e) => await SetViewModeAsync(CatalogViewMode.List);

    private async void GridModeButton_Click(object sender, RoutedEventArgs e) => await SetViewModeAsync(CatalogViewMode.Grid);

    private async void RefreshPreviewsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewCoordinator is not null)
        {
            await QueueVisibleSafelyAsync(force: true);
        }
    }

    private async Task QueueVisibleSafelyAsync(bool force)
    {
        try
        {
            await _previewCoordinator!.QueueVisibleAsync(force);
        }
        catch (OperationCanceledException)
        {
            // Deactivation or a mode switch superseded the visible preview request.
        }
    }

    private async Task SetViewModeAsync(CatalogViewMode viewMode)
    {
        if (_state.ViewMode == viewMode)
        {
            return;
        }

        _state = await _store.SaveAsync(_state with { ViewMode = viewMode });
        IsGridMode = viewMode == CatalogViewMode.Grid;
        UpdateViewModeControls();
        _catalogColumns = 0;
        UpdateCatalogColumns();
        if (IsGridMode)
        {
            await StartPreviewsAsync();
        }
        else if (_previewCoordinator is not null)
        {
            await _previewCoordinator.StopAsync();
        }
    }

    private void UpdateViewModeControls()
    {
        ListModeButton.IsEnabled = IsGridMode;
        GridModeButton.IsEnabled = !IsGridMode;
        RefreshPreviewsButton.Visibility = IsGridMode && GridPreviewFeature.CaptureEnabled && _state.UpdateStreamPreviews
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task StartPreviewsAsync()
    {
        if (_previewCoordinator is null || !_state.UpdateStreamPreviews || !_windowActive || !IsGridMode)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await _previewCoordinator.StartAsync();
    }

    private void ScheduleVisiblePreviewUpdate()
    {
        if (!_state.UpdateStreamPreviews || !IsGridMode || _previewCoordinator?.IsRunning != true)
        {
            return;
        }

        _viewportDebounce?.Cancel();
        _viewportDebounce?.Dispose();
        _viewportDebounce = new CancellationTokenSource();
        _ = QueueVisibleAfterLayoutAsync(_viewportDebounce.Token);
    }

    private async Task QueueVisibleAfterLayoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            await _previewCoordinator!.QueueVisibleAsync(force: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer viewport superseded this request.
        }
    }

    private IReadOnlyList<ChannelRow> GetVisibleRows()
    {
        var visible = new List<ChannelRow>();
        var viewport = new Rect(0, 0, StreamsList.ActualWidth, StreamsList.ActualHeight);
        for (var index = 0; index < GridRows.Count; index++)
        {
            if (StreamsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem container ||
                !container.IsVisible)
            {
                continue;
            }

            var bounds = container.TransformToAncestor(StreamsList)
                .TransformBounds(new Rect(container.RenderSize));
            if (bounds.IntersectsWith(viewport))
            {
                visible.AddRange(GridRows[index].Items);
            }
        }

        return visible;
    }

    private void ApplyPreview(string url, ImageSource image, bool? reachable)
    {
        foreach (var row in _rowCache.Values.Where(row => row.Channel.Url.Equals(url, StringComparison.Ordinal)))
        {
            row.SetPreview(image, reachable);
        }
    }

    private void ClearPreview(string url)
    {
        foreach (var row in _rowCache.Values.Where(row => row.Channel.Url.Equals(url, StringComparison.Ordinal)))
        {
            row.ClearPreview();
        }
    }

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        _windowActive = true;
        if (IsLoaded && IsGridMode)
        {
            await StartPreviewsAsync();
        }
    }

    private async void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        _windowActive = false;
        if (_previewCoordinator is not null)
        {
            await _previewCoordinator.StopAsync();
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _browsingSessionSaveTimer.Stop();
        await SaveBrowsingSessionAsync();
        _viewportDebounce?.Cancel();
        _viewportDebounce?.Dispose();
        if (_previewCoordinator is not null)
        {
            await _previewCoordinator.DisposeAsync();
        }
        _httpClient.Dispose();
    }
}
