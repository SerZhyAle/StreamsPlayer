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
        if (_previewCoordinator is null)
        {
            return;
        }

        // An explicit "capture now" has to make sure there is a session to capture in: the coordinator may
        // be suspended after a playback session, and a forced queue with no session only repaints.
        await StartPreviewsAsync();
        await QueueVisibleSafelyAsync(force: true);
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

        _state = await PersistAsync(_state with { ViewMode = viewMode });
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
        // The two mode buttons share one slot: the header offers the mode you can switch to, never the
        // one already active.
        ListModeButton.Visibility = IsGridMode ? Visibility.Visible : Visibility.Collapsed;
        GridModeButton.Visibility = IsGridMode ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Whether refreshing the grid previews means anything right now (SP-0050). It used to be the
    /// header button's visibility expression; the button is gone and the operations menu adds its entry
    /// only when this holds, so the condition survives the move instead of leaving an entry that
    /// silently does nothing in list mode.
    /// </summary>
    private bool CanRefreshPreviews => IsGridMode && GridPreviewFeature.CaptureEnabled && _state.UpdateStreamPreviews;

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
        // Not gated on IsRunning: repainting rows from the disk store is what makes scrolling work while
        // capture is suspended (window inactive, or a stream playing).
        if (!IsGridMode || _previewCoordinator is null)
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
        if (sender is not FrameworkElement { DataContext: ChannelRow row })
        {
            return;
        }

        row.SetTileHovered(true);
        if (_previewCoordinator?.IsRunning != true || !IsGridMode || !PreviewCapturePolicy.IsCaptureable(row.Channel))
        {
            return;
        }

        _hoverDwell?.Cancel();
        _hoverDwell?.Dispose();
        _hoverDwell = new CancellationTokenSource();
        _ = HoverCaptureAfterDwellAsync(row.Channel.Url, _hoverDwell.Token);
    }

    private void StreamTile_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChannelRow row })
        {
            row.SetTileHovered(false);
        }

        _hoverDwell?.Cancel();
    }

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

    /// <summary>
    /// The rows a preview would actually be seen on: what is realized and on screen, in both the pinned
    /// tile list and the main list.
    /// </summary>
    /// <remarks>
    /// SP-0067 changed both halves of this. The pinned half used to declare every pinned row visible, on
    /// the premise that the section was not virtualized - true when it was written, false now that
    /// pinning is uncapped and the section virtualizes, so it asks the same realized-container question
    /// as the main list. The main half used to walk all <c>GridRows.Count</c> entries asking the
    /// generator for a container; it now walks only the realized range the virtualizing panel reports.
    /// This runs on the scroll path via <c>ScheduleVisiblePreviewUpdate</c>, which is what makes it part
    /// of criterion 2.
    /// </remarks>
    private IReadOnlyList<ChannelRow> GetVisibleRows()
    {
        var started = BeginCatalogPerf();
        var visible = new List<ChannelRow>();
        var scanned = 0;

        if (HasPinned && !PinnedSectionCollapsed && IsGridMode)
        {
            scanned += CollectVisibleGridRows(PinnedGridList, visible);
        }

        scanned += CollectVisibleGridRows(StreamsList, visible);
        var result = visible.DistinctBy(row => row.Channel.Url).ToList();
        CatalogPerf("GetVisibleRows", started,
            $"scanned={scanned}", $"rows={result.Count}", $"catalogRows={GridRows.Count}");
        return result;
    }

    /// <summary>
    /// Adds the cards of every realized, on-screen row of <paramref name="list"/> to
    /// <paramref name="visible"/>, and returns how many rows it had to look at.
    /// </summary>
    /// <remarks>
    /// It iterates the virtualizing panel's own realized children rather than indexing the bound
    /// collection, which is what makes the count the viewport's instead of the catalog's - a recycling
    /// panel holds only what it has realized. Each container carries its row as its
    /// <c>DataContext</c>, so no index lookup is needed either. A list that has not been laid out yet
    /// has no panel and contributes nothing: a preview arriving one frame late is invisible, a full
    /// scan on every wheel notch is not.
    /// </remarks>
    private static int CollectVisibleGridRows(ListView list, List<ChannelRow> visible)
    {
        if (list.Visibility != Visibility.Visible || FindVirtualizingPanel(list) is not { } panel)
        {
            return 0;
        }

        var viewport = new Rect(0, 0, list.ActualWidth, list.ActualHeight);
        var scanned = 0;
        foreach (var child in panel.Children)
        {
            scanned++;
            if (child is not ListViewItem { IsVisible: true, DataContext: CatalogGridRow row } container)
            {
                continue;
            }

            var bounds = container.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
            if (bounds.IntersectsWith(viewport))
            {
                visible.AddRange(row.Items);
            }
        }

        return scanned;
    }

    private static VirtualizingStackPanel? FindVirtualizingPanel(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is VirtualizingStackPanel panel)
        {
            return panel;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindVirtualizingPanel(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // SP-0067: looked up, not scanned. Both of these ran a filter over every entry of _rowCache - up to
    // 19 855 of them - and ClearPreview is called on every eviction from the 192-entry memory cache,
    // which ordinary grid scrolling produces a steady stream of. The list value is kept because two
    // channels can carry the same URL (a MANUAL duplicate of a catalog row), which is exactly why the
    // original iterated instead of taking the first match.
    private void ApplyPreview(string url, ImageSource image, bool? reachable)
    {
        if (!_rowsByUrl.TryGetValue(url, out var rows))
        {
            return;
        }

        foreach (var row in rows)
        {
            row.SetPreview(image, reachable);
        }
    }

    private void ClearPreview(string url)
    {
        if (!_rowsByUrl.TryGetValue(url, out var rows))
        {
            return;
        }

        foreach (var row in rows)
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
        // SP-0040: quitting while a station plays is a normal way to end a listening session, and the
        // stop funnel is not on this path - without this the session the user was listening to when they
        // gave up on it would be the one session missing its summary in the archived log.
        EndAudioSession();
        // SP-0067: a filter change still inside its debounce would otherwise be dropped, and the session
        // saved from the state before the user's last keystroke. Flush it, then save.
        FlushPendingFilterEvaluation();
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
        _previewAtlasHttpClient.Dispose();
        _catalogHttpClient.Dispose();
    }
}
