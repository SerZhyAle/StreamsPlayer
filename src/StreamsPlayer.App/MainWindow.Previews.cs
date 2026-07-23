using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

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
        UpdatePinnedSectionLayout();
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
        // The coordinator runs in Grid mode to *show* stored thumbnails (capture is gated separately by the setting).
        // Never run while something is playing: a background LibVLC decode competes with the player.
        if (_previewCoordinator is null || !_windowActive || !IsGridMode ||
            _openPlayerWindows > 0 || _playingAudio is not null)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await _previewCoordinator.StartAsync();
    }

    private void ScheduleVisiblePreviewUpdate()
    {
        if (!IsGridMode || _previewCoordinator?.IsRunning != true)
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

    private void StreamTile_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_previewCoordinator?.IsRunning != true || !IsGridMode ||
            sender is not FrameworkElement { DataContext: ChannelRow row } ||
            !PreviewCapturePolicy.IsCaptureable(row.Channel))
        {
            return;
        }

        _hoverDwell?.Cancel();
        _hoverDwell?.Dispose();
        _hoverDwell = new CancellationTokenSource();
        _ = HoverCaptureAfterDwellAsync(row.Channel.Url, _hoverDwell.Token);
    }

    private void StreamTile_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _hoverDwell?.Cancel();

    private async Task HoverCaptureAfterDwellAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            _previewCoordinator!.RequestHoverCapture(url);
        }
        catch (OperationCanceledException)
        {
            // The cursor left the tile before the dwell elapsed, or previews stopped.
        }
    }

    private IReadOnlyList<ChannelRow> GetVisibleRows()
    {
        var visible = new List<ChannelRow>();

        // The pinned section is a small, non-virtualized region; when it is expanded every pinned tile
        // is realized, so treat them all as visible (SP-0025 — previews must span both regions).
        if (HasPinned && !PinnedSectionCollapsed && IsGridMode)
        {
            visible.AddRange(PinnedRows);
        }

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

        return visible.DistinctBy(row => row.Channel.Url).ToList();
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
        _hoverDwell?.Cancel();
        _hoverDwell?.Dispose();
        if (_previewCoordinator is not null)
        {
            await _previewCoordinator.DisposeAsync();
        }
        _audioRecoveryCts?.Cancel(); // abort any pending audio recovery so it never touches a disposing window
        StopNowPlayingMetadata();
        DisposeSystemMediaControls(); // SP-0021: end the Windows media session with the window
        _httpClient.Dispose();
        _icyHttpClient.Dispose();
    }
}
