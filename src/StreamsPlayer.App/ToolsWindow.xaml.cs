using System.Globalization;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0109: the operations that do something the moment they are pressed - catalog import and cleanup,
/// playlists, hidden channels, the FlyleafLib components and the log report. They moved here from
/// <see cref="SettingsWindow"/>, whose Cancel could not undo any of them (<c>APP-BEHAVIOUR</c> rule 12).
/// </summary>
/// <remarks>
/// The window only routes: the owning window holds the catalog state and the log, so it runs every
/// action, and passes this window back in as the owner of whatever picker, prompt or message the action
/// shows.
/// </remarks>
public partial class ToolsWindow : Window
{
    private readonly Func<ToolsAction, Window, Task> _runAction;
    private readonly Func<TvScheduleIndex> _tvSchedule;
    private bool _actionRunning;
    private CancellationTokenSource? _installCancellation;

    internal ToolsWindow(Func<ToolsAction, Window, Task> runAction, Func<TvScheduleIndex> tvSchedule)
    {
        InitializeComponent();
        _runAction = runAction;
        _tvSchedule = tvSchedule;
        ShowVideoComponents();
        TvScheduleAddressBox.Text = tvSchedule().Document?.SourceUrl ?? string.Empty;
        ShowTvSchedule();

        // SP-0052: a build without a snapshot says so rather than failing when pressed.
        if (!BundledCatalogSnapshot.Exists)
        {
            ApplyCatalogSnapshotButton.IsEnabled = false;
            ApplyCatalogSnapshotButton.ToolTip = LocalizationService.Get("CatalogSnapshotUnavailable");
        }
    }

    /// <summary>
    /// SP-0026: restates the components state after every install or removal, so the engine choice in
    /// Settings never silently means nothing.
    /// </summary>
    private void ShowVideoComponents()
    {
        var installed = FFmpegComponents.IsInstalled(FFmpegComponents.ResolveFolder(AppPaths.DataDirectory));
        VideoComponentsStatusText.Text = installed
            ? LocalizationService.Get("VideoComponentsInstalled")
            : LocalizationService.Format(
                "VideoComponentsMissing", FFmpegComponentsInstaller.ApproximateDownloadMegabytes);
        VideoComponentsInstallButton.IsEnabled = !installed;
        VideoComponentsRemoveButton.IsEnabled = installed;
    }

    /// <summary>
    /// Replaces the status line with the running byte count. The archive is ~67 MB, so a window that
    /// merely froze until it finished would be indistinguishable from a hang.
    /// </summary>
    internal void ShowInstallProgress(FFmpegInstallProgress progress)
    {
        VideoComponentsStatusText.Text = progress.Fraction is { } fraction
            ? LocalizationService.Format("VideoComponentsProgress", (int)(fraction * 100))
            : LocalizationService.Format("VideoComponentsProgressUnknown", progress.ReceivedBytes / (1024 * 1024));
    }

    /// <summary>
    /// SP-0128: enters the download state - the content is disabled, the Cancel button appears - and hands
    /// the owner the token that Cancel, or closing this window, trips.
    /// </summary>
    internal CancellationToken BeginVideoComponentsInstall()
    {
        _installCancellation?.Dispose();
        _installCancellation = new CancellationTokenSource();
        ToolsContent.IsEnabled = false;
        VideoComponentsCancelButton.IsEnabled = true;
        VideoComponentsCancelButton.Visibility = Visibility.Visible;
        return _installCancellation.Token;
    }

    internal void EndVideoComponentsInstall()
    {
        VideoComponentsCancelButton.Visibility = Visibility.Collapsed;
        ToolsContent.IsEnabled = true;
        _installCancellation?.Dispose();
        _installCancellation = null;
        ShowVideoComponents();
    }

    private void VideoComponentsCancel_Click(object sender, RoutedEventArgs e)
    {
        VideoComponentsCancelButton.IsEnabled = false;
        _installCancellation?.Cancel();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Close is the exit that changes nothing (rule 1), so it abandons a download still in flight.
        _installCancellation?.Cancel();
        base.OnClosing(e);
    }

    /// <summary>The address as typed; the owner validates it before anything is fetched.</summary>
    internal string TvScheduleAddress => TvScheduleAddressBox.Text.Trim();

    /// <summary>
    /// SP-0075: states what the schedule covers, and says plainly that unmatched channels simply show no
    /// programme - partial coverage is normal for a schedule and must not read as a fault.
    /// </summary>
    private void ShowTvSchedule()
    {
        var schedule = _tvSchedule();
        if (!schedule.HasSchedule)
        {
            TvScheduleStatusText.Text = LocalizationService.Get("TvScheduleStatusNone");
        }
        else if (schedule.CoverageEnd is not { } end || end <= DateTimeOffset.Now)
        {
            TvScheduleStatusText.Text = LocalizationService.Get("TvScheduleStatusEnded");
        }
        else
        {
            TvScheduleStatusText.Text = LocalizationService.Format(
                "TvScheduleStatusLoaded",
                schedule.MatchedCount,
                schedule.EligibleCount,
                end.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture));
        }

        TvScheduleRemoveButton.IsEnabled = schedule.HasSchedule || schedule.Bindings.Count > 0;
    }

    private void TvScheduleDownload_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(TvScheduleDownload_Click), () => RunAsync(ToolsAction.DownloadTvSchedule));

    private void TvScheduleRemove_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(TvScheduleRemove_Click), async () =>
        {
            await RunAsync(ToolsAction.RemoveTvSchedule);
            ShowTvSchedule();
        });

    /// <param name="lockWindow">
    /// <c>false</c> only for the components download, which disables the content itself so that its
    /// Cancel button, outside the content, stays live (SP-0128).
    /// </param>
    private async Task RunAsync(ToolsAction action, bool lockWindow = true)
    {
        if (_actionRunning)
        {
            return;
        }

        _actionRunning = true;
        IsEnabled = !lockWindow;
        try
        {
            await _runAction(action, this);
        }
        finally
        {
            IsEnabled = true;
            _actionRunning = false;
        }
    }

    private async void VideoComponentsInstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunAsync(ToolsAction.InstallVideoComponents, lockWindow: false);
            ShowVideoComponents();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(VideoComponentsInstall_Click), exception);
        }
    }

    private async void VideoComponentsRemove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunAsync(ToolsAction.RemoveVideoComponents);
            ShowVideoComponents();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(VideoComponentsRemove_Click), exception);
        }
    }

    private void ImportFromFile_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ImportFromFile_Click), () => RunAsync(ToolsAction.ImportFromFile));

    private void ImportFromUrl_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ImportFromUrl_Click), () => RunAsync(ToolsAction.ImportFromUrl));

    private void ExportAll_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExportAll_Click), () => RunAsync(ToolsAction.ExportAll));

    private void ExportPinned_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExportPinned_Click), () => RunAsync(ToolsAction.ExportPinned));

    private void ManageHidden_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ManageHidden_Click), () => RunAsync(ToolsAction.ManageHidden));

    private void ImportCatalogFromFile_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ImportCatalogFromFile_Click), () => RunAsync(ToolsAction.ImportCatalogFromFile));

    private void ApplyCatalogSnapshot_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ApplyCatalogSnapshot_Click), () => RunAsync(ToolsAction.ApplyCatalogSnapshot));

    private void DeleteDownloaded_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(DeleteDownloaded_Click), () => RunAsync(ToolsAction.DeleteDownloaded));

    private void DeleteImportedCatalog_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(DeleteImportedCatalog_Click), () => RunAsync(ToolsAction.DeleteImportedCatalog));

    private void SendLogs_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(SendLogs_Click), () => RunAsync(ToolsAction.SendLogsToAuthor));
}
