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

    internal ToolsWindow(Func<ToolsAction, Window, Task> runAction)
    {
        InitializeComponent();
        _runAction = runAction;
        ShowVideoComponents();

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

    internal void SetVideoComponentsBusy(bool busy)
    {
        VideoComponentsInstallButton.IsEnabled = !busy;
        VideoComponentsRemoveButton.IsEnabled = false;
        if (!busy)
        {
            ShowVideoComponents();
        }
    }

    private async void VideoComponentsInstall_Click(object sender, RoutedEventArgs e)
    {
        await _runAction(ToolsAction.InstallVideoComponents, this);
        ShowVideoComponents();
    }

    private async void VideoComponentsRemove_Click(object sender, RoutedEventArgs e)
    {
        await _runAction(ToolsAction.RemoveVideoComponents, this);
        ShowVideoComponents();
    }

    private async void ImportFromFile_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.ImportFromFile, this);

    private async void ImportFromUrl_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.ImportFromUrl, this);

    private async void ExportAll_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.ExportAll, this);

    private async void ExportPinned_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.ExportPinned, this);

    private async void ManageHidden_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.ManageHidden, this);

    private async void ImportCatalogFromFile_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.ImportCatalogFromFile, this);

    private async void ApplyCatalogSnapshot_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.ApplyCatalogSnapshot, this);

    private async void DeleteDownloaded_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.DeleteDownloaded, this);

    private async void DeleteImportedCatalog_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.DeleteImportedCatalog, this);

    private async void SendLogs_Click(object sender, RoutedEventArgs e) =>
        await _runAction(ToolsAction.SendLogsToAuthor, this);
}
