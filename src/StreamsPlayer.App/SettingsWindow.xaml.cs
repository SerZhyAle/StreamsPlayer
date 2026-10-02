using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0188: the unified settings surface holding both values and operations across 6 topic pages,
/// laid out by <c>APP-SETTINGS</c> and <c>APP-BEHAVIOUR</c>. Values apply on touch; Close is the sole exit.
/// </summary>
public partial class SettingsWindow : Window
{
    private static int s_lastSelectedPageIndex = 0;

    private readonly MainWindow _mainWindow;
    private readonly Func<ToolsAction, Window, Task> _runAction;
    private readonly Func<TvScheduleIndex> _tvSchedule;
    private readonly Dictionary<CaptureKind, string?> _captureFolders = [];
    private readonly bool _initialResumePlaybackOnStartup;
    private bool _initializing = true;
    private bool _actionRunning;
    private bool _closeByRunningAction;
    private CancellationTokenSource? _installCancellation;

    internal SettingsWindow(MainWindow mainWindow, Func<ToolsAction, Window, Task> runAction, Func<TvScheduleIndex> tvSchedule, int? initialPageIndex = null)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _runAction = runAction;
        _tvSchedule = tvSchedule;
        _initialResumePlaybackOnStartup = mainWindow.State.ResumePlaybackOnStartup;

        PopulateOptions();

        _captureFolders[CaptureKind.VideoFrame] = _mainWindow.State.FrameFolder;
        _captureFolders[CaptureKind.StreamVideo] = _mainWindow.State.VideoRecordingFolder;
        _captureFolders[CaptureKind.StreamAudio] = _mainWindow.State.AudioRecordingFolder;
        foreach (var kind in _captureFolders.Keys)
        {
            ShowCaptureFolder(kind);
        }

        VersionText.Text = ProductInfo.Version;
        AuthorText.Text = ProductInfo.Author;

        ShowVideoComponents();
        TvScheduleAddressBox.Text = tvSchedule().Document?.SourceUrl ?? string.Empty;
        ShowTvSchedule();

        if (!BundledCatalogSnapshot.Exists)
        {
            ApplyCatalogSnapshotButton.Visibility = Visibility.Collapsed;
        }

        var targetPageIndex = initialPageIndex ?? s_lastSelectedPageIndex;
        if (targetPageIndex >= 0 && targetPageIndex < SettingsTabControl.Items.Count)
        {
            SettingsTabControl.SelectedIndex = targetPageIndex;
        }

        _initializing = false;
    }

    private void PopulateOptions()
    {
        var currentLanguage = LocalizationService.CurrentLanguage;
        var languages = InterfaceLanguages.All
            .Select(entry => new UiOption(entry.Language.ToString(), LocalizationService.NativeName(entry.Language)))
            .ToArray();
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.FirstOrDefault(item => item.Value == currentLanguage.ToString()) ?? languages[0];

        var themes = new[]
        {
            new UiOption(nameof(AppTheme.System), LocalizationService.Get("ThemeSystem")),
            new UiOption(nameof(AppTheme.Light), LocalizationService.Get("ThemeLight")),
            new UiOption(nameof(AppTheme.Dark), LocalizationService.Get("ThemeDark"))
        };
        ThemeBox.ItemsSource = themes;
        ThemeBox.SelectedItem = themes.FirstOrDefault(item => item.Value == _mainWindow.State.Theme.ToString()) ?? themes[0];

        var sizes = new[]
        {
            new UiOption(nameof(StreamTileSize.VerySmall), LocalizationService.Get("TileVerySmall")),
            new UiOption(nameof(StreamTileSize.Small), LocalizationService.Get("TileSmall")),
            new UiOption(nameof(StreamTileSize.Medium), LocalizationService.Get("TileMedium")),
            new UiOption(nameof(StreamTileSize.Large), LocalizationService.Get("TileLarge"))
        };
        TileSizeBox.ItemsSource = sizes;
        TileSizeBox.SelectedItem = sizes.FirstOrDefault(item => item.Value == _mainWindow.State.TileSize.ToString()) ?? sizes[0];

        var backends = new[]
        {
            new UiOption(nameof(MediaBackend.LibVlc), LocalizationService.Get("VideoBackendLibVlc")),
            new UiOption(nameof(MediaBackend.Flyleaf), LocalizationService.Get("VideoBackendFlyleaf"))
        };
        VideoBackendBox.ItemsSource = backends;
        VideoBackendBox.SelectedItem = backends.FirstOrDefault(item => item.Value == _mainWindow.State.VideoBackend.ToString()) ?? backends[0];

        HideAdultContentCheckBox.IsChecked = _mainWindow.State.HideAdultContent;
        UpdatePreviewsCheckBox.IsChecked = _mainWindow.State.UpdateStreamPreviews;
        AnimatedBackdropCheckBox.IsChecked = _mainWindow.State.AnimatedBackdrop;
        KeepAwakeCheckBox.IsChecked = _mainWindow.State.KeepAwakeDuringPlayback;
        SystemMediaControlsCheckBox.IsChecked = _mainWindow.State.SystemMediaControls;
        ResumePlaybackCheckBox.IsChecked = _mainWindow.State.ResumePlaybackOnStartup;

        var audioDevices = AudioDeviceService.GetAvailableAudioDevices();
        AudioDeviceBox.ItemsSource = audioDevices;
        AudioDeviceBox.SelectedItem = audioDevices.FirstOrDefault(item => item.Value == (_mainWindow.State.AudioOutputDevice ?? string.Empty)) ?? audioDevices[0];

        var audioChannels = new[]
        {
            new UiOption(nameof(AudioChannelMode.Stereo), LocalizationService.Get("AudioChannelStereo")),
            new UiOption(nameof(AudioChannelMode.Left), LocalizationService.Get("AudioChannelLeft")),
            new UiOption(nameof(AudioChannelMode.Right), LocalizationService.Get("AudioChannelRight")),
            new UiOption(nameof(AudioChannelMode.ReverseStereo), LocalizationService.Get("AudioChannelReverseStereo")),
            new UiOption(nameof(AudioChannelMode.DolbySurround), LocalizationService.Get("AudioChannelDolby"))
        };
        AudioChannelBox.ItemsSource = audioChannels;
        AudioChannelBox.SelectedItem = audioChannels.FirstOrDefault(item => item.Value == _mainWindow.State.AudioChannelMode.ToString()) ?? audioChannels[0];
    }

    /// <summary>
    /// Re-populates code-built option strings, status texts, and updates layout direction when the UI language changes live.
    /// </summary>
    internal void ReapplyLocalization()
    {
        _initializing = true;
        FlowDirection = (FlowDirection)FindResource("UiFlowDirection");
        PopulateOptions();
        ShowTvSchedule();
        ShowVideoComponents();
        _initializing = false;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageBox.SelectedItem is not UiOption option)
        {
            return;
        }

        if (Enum.TryParse<AppLanguage>(option.Value, out var language) && language != LocalizationService.CurrentLanguage)
        {
            HandlerBoundary.Run(nameof(LanguageBox_SelectionChanged), async () =>
            {
                await _mainWindow.ChangeLanguageFromSettingsAsync(language);
                ReapplyLocalization();
            });
        }
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeBox.SelectedItem is not UiOption option)
        {
            return;
        }

        if (Enum.TryParse<AppTheme>(option.Value, out var theme))
        {
            HandlerBoundary.Run(nameof(ThemeBox_SelectionChanged), () => _mainWindow.ApplyThemeFromSettingsAsync(theme));
        }
    }

    private void TileSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TileSizeBox.SelectedItem is not UiOption option)
        {
            return;
        }

        if (Enum.TryParse<StreamTileSize>(option.Value, out var size))
        {
            HandlerBoundary.Run(nameof(TileSizeBox_SelectionChanged), () => _mainWindow.ApplyTileSizeFromSettingsAsync(size));
        }
    }

    private void AnimatedBackdropCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        HandlerBoundary.Run(nameof(AnimatedBackdropCheckBox_Click),
            () => _mainWindow.ApplyAnimatedBackdropFromSettingsAsync(AnimatedBackdropCheckBox.IsChecked == true));
    }

    private void HideAdultContentCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        HandlerBoundary.Run(nameof(HideAdultContentCheckBox_Click),
            () => _mainWindow.ApplyHideAdultContentFromSettingsAsync(HideAdultContentCheckBox.IsChecked == true));
    }

    private void UpdatePreviewsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        HandlerBoundary.Run(nameof(UpdatePreviewsCheckBox_Click),
            () => _mainWindow.ApplyUpdatePreviewsFromSettingsAsync(UpdatePreviewsCheckBox.IsChecked == true));
    }

    private void KeepAwakeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        HandlerBoundary.Run(nameof(KeepAwakeCheckBox_Click),
            () => _mainWindow.ApplyKeepAwakeFromSettingsAsync(KeepAwakeCheckBox.IsChecked == true));
    }

    private void SystemMediaControlsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        HandlerBoundary.Run(nameof(SystemMediaControlsCheckBox_Click),
            () => _mainWindow.ApplySystemMediaControlsFromSettingsAsync(SystemMediaControlsCheckBox.IsChecked == true));
    }

    private void ResumePlaybackCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        HandlerBoundary.Run(nameof(ResumePlaybackCheckBox_Click),
            () => _mainWindow.ApplyResumePlaybackFromSettingsAsync(ResumePlaybackCheckBox.IsChecked == true));
    }

    private void VideoBackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || VideoBackendBox.SelectedItem is not UiOption option)
        {
            return;
        }

        if (Enum.TryParse<MediaBackend>(option.Value, out var backend))
        {
            if (backend == MediaBackend.Flyleaf
                && !FFmpegComponents.IsInstalled(FFmpegComponents.ResolveFolder(AppPaths.DataDirectory)))
            {
                if (MessageBox.Show(
                        this,
                        LocalizationService.Get("VideoComponentsRequiredBody"),
                        LocalizationService.Get("VideoComponentsRequiredTitle"),
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning,
                        MessageBoxResult.Cancel) != MessageBoxResult.OK)
                {
                    _initializing = true;
                    VideoBackendBox.SelectedItem = ((IEnumerable<UiOption>)VideoBackendBox.ItemsSource).First(x => x.Value == nameof(MediaBackend.LibVlc));
                    _initializing = false;
                    return;
                }
            }

            HandlerBoundary.Run(nameof(VideoBackendBox_SelectionChanged),
                () => _mainWindow.ApplyVideoBackendFromSettingsAsync(backend));
        }
    }

    private void AudioDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || AudioDeviceBox.SelectedItem is not UiOption option)
        {
            return;
        }

        var deviceId = string.IsNullOrEmpty(option.Value) ? null : option.Value;
        HandlerBoundary.Run(nameof(AudioDeviceBox_SelectionChanged),
            () => _mainWindow.ApplyAudioOutputDeviceFromSettingsAsync(deviceId));
    }

    private void AudioChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || AudioChannelBox.SelectedItem is not UiOption option)
        {
            return;
        }

        if (Enum.TryParse<AudioChannelMode>(option.Value, out var channelMode))
        {
            HandlerBoundary.Run(nameof(AudioChannelBox_SelectionChanged),
                () => _mainWindow.ApplyAudioChannelModeFromSettingsAsync(channelMode));
        }
    }

    private static CaptureKind KindOf(object sender) =>
        sender is FrameworkElement { Tag: string tag } && Enum.TryParse<CaptureKind>(tag, out var kind) ? kind : CaptureKind.VideoFrame;

    private string ResolvedFolder(CaptureKind kind) => _captureFolders[kind] ?? CaptureFolders.Default(kind);

    private void ShowCaptureFolder(CaptureKind kind)
    {
        var box = kind switch
        {
            CaptureKind.StreamVideo => VideoRecordingFolderBox,
            CaptureKind.StreamAudio => AudioRecordingFolderBox,
            _ => FrameFolderBox
        };
        box.Text = ResolvedFolder(kind);
    }

    private void FrameFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var kind = KindOf(sender);
        var current = ResolvedFolder(kind);
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get(kind switch
            {
                CaptureKind.StreamVideo => "VideoRecordingFolderLabel",
                CaptureKind.StreamAudio => "AudioRecordingFolderLabel",
                _ => "FrameFolderLabel"
            }),
            InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current) ?? current,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _captureFolders[kind] = dialog.FolderName;
            ShowCaptureFolder(kind);
            HandlerBoundary.Run(nameof(FrameFolderBrowse_Click),
                () => _mainWindow.ApplyCaptureFolderFromSettingsAsync(kind, dialog.FolderName));
        }
    }

    private void FrameFolderReset_Click(object sender, RoutedEventArgs e)
    {
        var kind = KindOf(sender);
        _captureFolders[kind] = null;
        ShowCaptureFolder(kind);
        HandlerBoundary.Run(nameof(FrameFolderReset_Click),
            () => _mainWindow.ApplyCaptureFolderFromSettingsAsync(kind, null));
    }

    private void FrameFolderOpen_Click(object sender, RoutedEventArgs e)
    {
        var folder = ResolvedFolder(KindOf(sender));
        if (!LogReportMailer.OpenFolder(folder))
        {
            MessageBox.Show(this, LocalizationService.Format("LogArchiveOpenFolderFailed", folder), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        var currentLanguage = LocalizationService.CurrentLanguage;
        var url = (sender as FrameworkContentElement)?.Tag switch
        {
            "Instructions" => ProductInfo.InstructionsUrl(currentLanguage),
            "Source" => ProductInfo.SourceUrl,
            "Website" => ProductInfo.WebsiteUrl,
            "Privacy" => ProductInfo.PrivacyUrl,
            "Author" => ProductInfo.AuthorUrl,
            "Licence" => ProductInfo.LicenceUrl,
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

    internal void ShowInstallProgress(FFmpegInstallProgress progress)
    {
        VideoComponentsStatusText.Text = progress.Fraction is { } fraction
            ? LocalizationService.Format("VideoComponentsProgress", (int)(fraction * 100))
            : LocalizationService.Format("VideoComponentsProgressUnknown", progress.ReceivedBytes / (1024 * 1024));
    }

    internal CancellationToken BeginVideoComponentsInstall()
    {
        _installCancellation?.Dispose();
        _installCancellation = new CancellationTokenSource();
        SettingsContent.IsEnabled = false;
        VideoComponentsCancelButton.IsEnabled = true;
        VideoComponentsCancelButton.Visibility = Visibility.Visible;
        return _installCancellation.Token;
    }

    internal void EndVideoComponentsInstall()
    {
        VideoComponentsCancelButton.Visibility = Visibility.Collapsed;
        SettingsContent.IsEnabled = true;
        _installCancellation?.Dispose();
        _installCancellation = null;
        ShowVideoComponents();
    }

    private void VideoComponentsCancel_Click(object sender, RoutedEventArgs e)
    {
        VideoComponentsCancelButton.IsEnabled = false;
        _installCancellation?.Cancel();
    }

    internal void CloseForRunningAction()
    {
        _closeByRunningAction = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        s_lastSelectedPageIndex = SettingsTabControl.SelectedIndex;

        if (_actionRunning && !_closeByRunningAction)
        {
            if (_installCancellation is not null)
            {
                VideoComponentsCancelButton.IsEnabled = false;
                _installCancellation.Cancel();
            }
            else
            {
                e.Cancel = true;
                MessageBox.Show(
                    this,
                    LocalizationService.Get("SettingsWindowBusyClose"),
                    LocalizationService.Get("SettingsWindowTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                base.OnClosing(e);
                return;
            }
        }

        if (!e.Cancel && _initialResumePlaybackOnStartup && !_mainWindow.State.ResumePlaybackOnStartup)
        {
            HandlerBoundary.Run(nameof(OnClosing), () => _mainWindow.ClearResumeHistoryIfDisabledAsync());
        }

        base.OnClosing(e);
    }

    internal string TvScheduleAddress => TvScheduleAddressBox.Text.Trim();

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

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
