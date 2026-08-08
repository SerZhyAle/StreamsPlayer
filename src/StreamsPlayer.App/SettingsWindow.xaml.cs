using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class SettingsWindow : Window
{
    private readonly AppLanguage _language;
    private readonly StreamChannel? _selectedChannel;
    private readonly Func<SettingsAction, Window, Task> _runSettingsAction;
    // SP-0038: null means "unset", which resolves to Downloads at save time. The text box always shows a
    // real path so the user can see where frames land either way, hence the separate field.
    private string? _frameFolder;

    public SettingsWindow(AppTheme theme, StreamTileSize tileSize, bool updateStreamPreviews, bool keepAwakeDuringPlayback, bool systemMediaControls, bool resumePlaybackOnStartup, MediaBackend videoBackend, string? frameFolder, AppLanguage language, StreamChannel? selectedChannel, Func<SettingsAction, Window, Task> runSettingsAction)
    {
        InitializeComponent();
        _language = language;
        _selectedChannel = selectedChannel;
        _runSettingsAction = runSettingsAction;
        var themes = new[]
        {
            new UiOption(nameof(AppTheme.System), LocalizationService.Get("ThemeSystem")),
            new UiOption(nameof(AppTheme.Light), LocalizationService.Get("ThemeLight")),
            new UiOption(nameof(AppTheme.Dark), LocalizationService.Get("ThemeDark"))
        };
        ThemeBox.ItemsSource = themes;
        ThemeBox.SelectedItem = themes.First(item => item.Value == theme.ToString());
        var sizes = new[]
        {
            new UiOption(nameof(StreamTileSize.VerySmall), LocalizationService.Get("TileVerySmall")),
            new UiOption(nameof(StreamTileSize.Small), LocalizationService.Get("TileSmall")),
            new UiOption(nameof(StreamTileSize.Medium), LocalizationService.Get("TileMedium")),
            new UiOption(nameof(StreamTileSize.Large), LocalizationService.Get("TileLarge"))
        };
        TileSizeBox.ItemsSource = sizes;
        TileSizeBox.SelectedItem = sizes.First(item => item.Value == tileSize.ToString());
        UpdatePreviewsCheckBox.IsChecked = updateStreamPreviews;
        KeepAwakeCheckBox.IsChecked = keepAwakeDuringPlayback;
        SystemMediaControlsCheckBox.IsChecked = systemMediaControls;
        ResumePlaybackCheckBox.IsChecked = resumePlaybackOnStartup;
        var backends = new[]
        {
            new UiOption(nameof(MediaBackend.LibVlc), LocalizationService.Get("VideoBackendLibVlc")),
            new UiOption(nameof(MediaBackend.Flyleaf), LocalizationService.Get("VideoBackendFlyleaf"))
        };
        VideoBackendBox.ItemsSource = backends;
        VideoBackendBox.SelectedItem = backends.First(item => item.Value == videoBackend.ToString());
        ShowVideoComponents();
        _frameFolder = frameFolder;
        ShowFrameFolder();
        VersionText.Text = ProductInfo.Version;
        AuthorText.Text = ProductInfo.Author;
        SelectedStreamText.Text = selectedChannel is null
            ? LocalizationService.Get("NoStreamSelected")
            : StreamTitleFormatter.Display(selectedChannel.Title);
        CopyLaunchCommandButton.IsEnabled = selectedChannel is not null;
        CreateDesktopShortcutButton.IsEnabled = selectedChannel is not null;

        // SP-0052: offered unconditionally when the build carries a snapshot - it works the same whether
        // the catalog is empty, snapshot-filled or downloaded. A build without one says so rather than
        // failing when pressed.
        if (!BundledCatalogSnapshot.Exists)
        {
            ApplyCatalogSnapshotButton.IsEnabled = false;
            ApplyCatalogSnapshotButton.ToolTip = LocalizationService.Get("CatalogSnapshotUnavailable");
        }

        var choices = InterfaceLanguages.All
            .Select(entry => new LanguageChoice(
                entry.Language,
                LocalizationService.NativeName(entry.Language),
                entry.Language == language))
            .ToArray();
        LanguageList.ItemsSource = choices;
        LanguageList.SelectedItem = choices.FirstOrDefault(choice => choice.IsActive) ?? choices[0];
        Loaded += (_, _) => LanguageList.ScrollIntoView(LanguageList.SelectedItem);
    }

    /// <summary>
    /// The language the user picked, or <c>null</c> when it is the one already in use. Returning
    /// <c>null</c> for the active language keeps the standalone picker's semantic: confirming the
    /// language you are already reading is not a change and must not rewrite state or re-render the
    /// interface.
    /// </summary>
    internal AppLanguage? SelectedLanguage =>
        LanguageList.SelectedItem is LanguageChoice { IsActive: false } choice ? choice.Language : null;

    public AppTheme SelectedTheme => Enum.Parse<AppTheme>(((UiOption)ThemeBox.SelectedItem).Value);
    public StreamTileSize SelectedTileSize => Enum.Parse<StreamTileSize>(((UiOption)TileSizeBox.SelectedItem).Value);
    public bool UpdateStreamPreviews => UpdatePreviewsCheckBox.IsChecked == true;
    public bool KeepAwakeDuringPlayback => KeepAwakeCheckBox.IsChecked == true;
    public bool SystemMediaControls => SystemMediaControlsCheckBox.IsChecked == true;
    public bool ResumePlaybackOnStartup => ResumePlaybackCheckBox.IsChecked == true;
    public MediaBackend SelectedVideoBackend => Enum.Parse<MediaBackend>(((UiOption)VideoBackendBox.SelectedItem).Value);

    /// <summary>The chosen frames folder, or <c>null</c> for "wherever Downloads is at save time".</summary>
    public string? FrameFolder => _frameFolder;

    private void ShowFrameFolder() => FrameFolderBox.Text = CapturedFrameWriter.ResolveFolder(_frameFolder);

    private void FrameFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get("FrameFolderLabel"),
            InitialDirectory = CapturedFrameWriter.ResolveFolder(_frameFolder),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _frameFolder = dialog.FolderName;
            ShowFrameFolder();
        }
    }

    private void FrameFolderReset_Click(object sender, RoutedEventArgs e)
    {
        _frameFolder = null;
        ShowFrameFolder();
    }

    /// <summary>
    /// SP-0026 - restates the components state after every install or removal. The engine ComboBox on
    /// its own would let the user select an engine that cannot start, so this line is what makes the
    /// choice above mean something.
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
    /// Replaces the status line with the running byte count. The archive is ~67 MB, so a dialog that
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

    // The download commits on its own like the import and delete actions above: closing Settings with
    // Cancel does not uninstall what was just fetched.
    private async void VideoComponentsInstall_Click(object sender, RoutedEventArgs e)
    {
        await _runSettingsAction(SettingsAction.InstallVideoComponents, this);
        ShowVideoComponents();
    }

    private async void VideoComponentsRemove_Click(object sender, RoutedEventArgs e)
    {
        await _runSettingsAction(SettingsAction.RemoveVideoComponents, this);
        ShowVideoComponents();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Decision 6: saving FlyleafLib without its components would leave the player quietly running
        // on LibVLC, so the user hears about it here rather than wondering why nothing changed.
        if (SelectedVideoBackend == MediaBackend.Flyleaf
            && !FFmpegComponents.IsInstalled(FFmpegComponents.ResolveFolder(AppPaths.DataDirectory))
            && MessageBox.Show(
                this,
                LocalizationService.Get("VideoComponentsRequiredBody"),
                LocalizationService.Get("VideoComponentsRequiredTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void ImportFromFile_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.ImportFromFile, this);

    private async void ImportFromUrl_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.ImportFromUrl, this);

    private async void ExportAll_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.ExportAll, this);

    private async void ExportPinned_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.ExportPinned, this);

    // Immediate like the delete below: unhiding a channel commits on its own, so Cancel here does not
    // undo it.
    private async void ManageHidden_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.ManageHidden, this);

    // SP-0030: destructive and immediate - the confirmation inside the action is the commit point,
    // so closing Settings with Cancel does not bring the downloaded rows back.
    // SP-0052: immediate like the two above - applying the bundled snapshot commits on its own, and
    // closing Settings with Cancel does not take the channels back out.
    private async void ApplyCatalogSnapshot_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.ApplyCatalogSnapshot, this);

    private async void DeleteDownloaded_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.DeleteDownloaded, this);

    private void CopyLaunchCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedChannel is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(StreamShortcutService.BuildLaunchCommand(_selectedChannel.Id));
            MessageBox.Show(this, LocalizationService.Get("LaunchCommandCopied"), Title, MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (COMException)
        {
            MessageBox.Show(this, LocalizationService.Get("LaunchCommandCopyFailed"), Title, MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CreateDesktopShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedChannel is null)
        {
            return;
        }

        try
        {
            var path = StreamShortcutService.CreateDesktopShortcut(_selectedChannel);
            MessageBox.Show(this, LocalizationService.Format("DesktopShortcutCreated", path), Title, MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, LocalizationService.Get("DesktopShortcutFailed"), Title, MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // SP-0040: the owning window holds the catalog state and the log, so it builds and sends the report.
    private async void SendLogs_Click(object sender, RoutedEventArgs e) =>
        await _runSettingsAction(SettingsAction.SendLogsToAuthor, this);

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as FrameworkContentElement)?.Tag switch
        {
            "Instructions" => ProductInfo.InstructionsUrl(_language),
            "Source" => ProductInfo.SourceUrl,
            "Website" => ProductInfo.WebsiteUrl,
            "Privacy" => ProductInfo.PrivacyUrl,
            "Author" => ProductInfo.AuthorUrl,
            _ => null
        };
        if (url is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, LocalizationService.Get("SettingsOpenLinkFailed"),
                LocalizationService.Get("SettingsOpenLinkFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
