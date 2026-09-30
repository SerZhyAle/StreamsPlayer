using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // SP-0119: the preview engine failed to start in the constructor; previews are off for the session.
    private bool _gridPreviewsUnavailable;
    private bool _gridPreviewsUnavailableReported;

    private void ListModeButton_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(ListModeButton_Click), () => SetViewModeAsync(CatalogViewMode.List));

    private void GridModeButton_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(GridModeButton_Click), () => SetViewModeAsync(CatalogViewMode.Grid));

    private async void RefreshPreviewsButton_Click(object sender, RoutedEventArgs e)
    {
        try
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
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(RefreshPreviewsButton_Click), exception);
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
        if ((_stateCommitter?.Requested ?? _state).ViewMode == viewMode)
        {
            return;
        }

        await PersistAsync(state => state.ViewMode == viewMode ? state : state with { ViewMode = viewMode });
        IsGridMode = (_stateCommitter?.Requested ?? _state).ViewMode == CatalogViewMode.Grid;
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
        // SP-0102: both buttons remain visible in a segmented group; the active mode carries Tag="Active".
        ListModeButton.Visibility = Visibility.Visible;
        GridModeButton.Visibility = Visibility.Visible;
        ListModeButton.Tag = !IsGridMode ? "Active" : null;
        GridModeButton.Tag = IsGridMode ? "Active" : null;
    }

    /// <summary>
    /// Whether refreshing the grid previews means anything right now (SP-0050). It used to be the
    /// header button's visibility expression; the button is gone and the operations menu adds its entry
    /// only when this holds, so the condition survives the move instead of leaving an entry that
    /// silently does nothing in list mode.
    /// </summary>
    private bool CanRefreshPreviews =>
        IsGridMode && GridPreviewFeature.CaptureEnabled && _previewCoordinator is not null && _state.UpdateStreamPreviews;

    /// <summary>
    /// Starts the grid-preview engine, or returns null when it cannot start (SP-0119).
    /// </summary>
    /// <remarks>
    /// This runs in the window's constructor, so an engine that throws - a portable copy without its native
    /// folder, a plugin quarantined by antivirus - used to end the process at startup with nothing on screen,
    /// before the catalog or the lazily started radio engine ever had a chance. Previews are the one feature
    /// that needs this engine, so they are what is switched off; grid mode says so once.
    /// </remarks>
    private VideoFrameCaptureService? TryStartGridPreviewEngine()
    {
        try
        {
            return new VideoFrameCaptureService((category, fields) => _log.Event(category, fields));
        }
        catch (Exception exception)
        {
            _gridPreviewsUnavailable = true;
            _log.Event("GRID PREVIEWS UNAVAILABLE", $"type={exception.GetType().Name}");
            _log.Error("Grid preview engine failed to start", exception);
            return null;
        }
    }

    /// <summary>The one notice that grid previews are off for this session because their engine failed.</summary>
    private void ReportGridPreviewsUnavailableOnce()
    {
        if (!_gridPreviewsUnavailable || _gridPreviewsUnavailableReported || !IsGridMode)
        {
            return;
        }

        _gridPreviewsUnavailableReported = true;
        // A message rather than the status line: the status is rewritten by the channel count moments later,
        // so a status notice was gone before anyone could read it. Posted at idle so the load, a requested
        // launch and the first-run offers are not held behind the user's click.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_shuttingDown || !IsVisible)
            {
                return;
            }

            MessageBox.Show(this, LocalizationService.Get("GridPreviewsUnavailable"), LocalizationService.Get("ProductName"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private async Task StartPreviewsAsync()
    {
        ReportGridPreviewsUnavailableOnce();
        // The coordinator runs in Grid mode to *show* stored thumbnails (capture is gated separately by the setting).
        // Never run while something is playing: a background LibVLC decode competes with the player.
        // SP-0065: _shuttingDown first, because this is the funnel the late callers reach - the last owned
        // PlayerWindow closing during teardown runs its Closed handler, which lands here.
        if (_shuttingDown || _previewCoordinator is null || !_windowActive || !IsGridMode ||
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
        if (_shuttingDown || !IsGridMode || _previewCoordinator is null)
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
        if (_shuttingDown || sender is not FrameworkElement { DataContext: ChannelRow row })
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

    // SP-0065: destroying the window disposes the mouse input provider, which synthesizes a final
    // MouseLeave for whatever the pointer was over. That arrives after MainWindow_Closed has disposed
    // _hoverDwell, and Cancel on a disposed source throws - an unhandled exception that skipped App.OnExit
    // and with it WakeGuard.Reset. There is no hover state worth updating in a window that is closing.
    private void StreamTile_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_shuttingDown)
        {
            return;
        }

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
        try
        {
            _windowActive = true;
            if (IsLoaded && IsGridMode)
            {
                await StartPreviewsAsync();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MainWindow_Activated), exception);
        }
    }

    private async void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        try
        {
            _windowActive = false;
            // SP-0065: closing deactivates, and this one does not go through StartPreviewsAsync's funnel.
            if (_shuttingDown || _previewCoordinator is null)
            {
                return;
            }

            await _previewCoordinator.StopAsync();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MainWindow_Deactivated), exception);
        }
    }

    /// <summary>
    /// SP-0120: the close work - session save and disposals - as a task the application waits for before it ends
    /// the process. It used to run in an <c>async void</c> handler that nothing waited for, so the process could
    /// exit at its first await with the disposals below never reached. Completed until the window has closed.
    /// </summary>
    internal Task CloseWork { get; private set; } = Task.CompletedTask;

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // SP-0065: before the first await - already set by MainWindow_Closing, restated here because this is the
        // statement everything below depends on. The dispatcher keeps delivering input while the work saves.
        _shuttingDown = true;
        // SP-0120: before any await, for the same reason: a recovery backoff that ends during the saves below
        // would otherwise restart a station in a window that is closing.
        _audioRecoveryCts?.Cancel();
        CloseWork = RunCloseWorkAsync();
    }

    private async Task RunCloseWorkAsync()
    {
        try
        {
            // SP-0040: quitting while a station plays is a normal way to end a listening session, and the
            // stop funnel is not on this path - without this the session the user was listening to when they
            // gave up on it would be the one session missing its summary in the archived log.
            await StopAudioRecordingForShutdownAsync(); // SP-0121: the file is closed before the process goes
            EndAudioSession();
            // SP-0067: a filter change still inside its debounce would otherwise be dropped, and the session
            // saved from the state before the user's last keystroke. Flush it, then save.
            FlushPendingFilterEvaluation();
            _browsingSessionSaveTimer.Stop();
            _audioVolumeSaveTimer.Stop();
            await FlushPendingAudioVolumeAsync();
            await FlushPendingAudioResumeAsync();
            _nowPlayingHistorySaveTimer.Stop();
            await FlushPendingNowPlayingHistoryAsync();
            // SP-0069: a started DispatcherTimer is rooted by the dispatcher and keeps its Tick target - this
            // window - alive. The sleep ticker repeats and stops itself only when its deadline arrives or the
            // user cancels it, so quitting with a sleep timer armed left one running against a closed window.
            StopSleepTicker();
            // force: the scroll-only rate limit must never be what decides whether the position the user
            // left the list at survives the session.
            await SaveBrowsingSessionAsync(force: true);
            _viewportDebounce?.Cancel();
            _viewportDebounce?.Dispose();
            _viewportDebounce = null;
            _hoverDwell?.Cancel();
            _hoverDwell?.Dispose();
            // SP-0065: nulled, not merely disposed. The latch above is the policy; this is the mechanical
            // guarantee that a handler which forgets to consult it cannot reach a disposed source.
            _hoverDwell = null;
            if (_previewCoordinator is not null)
            {
                await _previewCoordinator.DisposeAsync();
            }
            _standardAudioPlayback.Dispose();
            StopNowPlayingMetadata();
            DisposeSystemMediaControls(); // SP-0021: end the Windows media session with the window
            _httpClient.Dispose();
            _icyHttpClient.Dispose();
            _statusHttpClient.Dispose();
            _previewArtworkHttpClient.Dispose();
            _catalogHttpClient.Dispose();
            // SP-0120: last, so a slow native teardown costs nothing above. Waited for so the process does not
            // exit in the middle of one; the application bounds the whole close work, this included.
            await Task.WhenAll(_closedPlayerEngines);
            // SP-0164: then the recording finishes, each already past its engine release, so the move into the
            // recordings folder gets its chance. The move ends in a rename, so a miss of the deadline leaves
            // the staged original for the next start to hand over - never a partial file under the final name.
            await Task.WhenAll(_closedRecordingFinishes);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MainWindow_Closed), exception);
        }
    }
}
