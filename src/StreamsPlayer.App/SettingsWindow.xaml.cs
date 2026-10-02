using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// The preferences, committed together by Save and discarded together by Cancel.
/// </summary>
/// <remarks>
/// SP-0109, <c>APP-BEHAVIOUR</c> rule 12: a window that offers Cancel may not have done anything by the time
/// Cancel is pressed, so every control here either edits a pending value or opens something without changing
/// it. That is why this window takes no action delegate: an operation that commits on its own belongs in
/// <see cref="ToolsWindow"/>, and one that acts on a single channel belongs in that channel's menu.
/// <c>DesktopUxConformanceTests</c> holds the list of handlers this markup may use.
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly AppLanguage _language;
    // SP-0038 / SP-0179: the pending folder per captured kind. Null means "unset", which resolves to the kind's
    // default folder at capture time. The text box always shows a real path so the user can see where files
    // land either way, hence the separate values.
    private readonly Dictionary<CaptureKind, string?> _captureFolders = [];

    public SettingsWindow(AppTheme theme, StreamTileSize tileSize, bool updateStreamPreviews, bool hideAdultContent, bool animatedBackdrop, bool keepAwakeDuringPlayback, bool systemMediaControls, bool resumePlaybackOnStartup, MediaBackend videoBackend, string? frameFolder, string? videoRecordingFolder, string? audioRecordingFolder, AppLanguage language)
    {
        InitializeComponent();
        _language = language;
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
        HideAdultContentCheckBox.IsChecked = hideAdultContent;
        AnimatedBackdropCheckBox.IsChecked = animatedBackdrop;
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
        _captureFolders[CaptureKind.VideoFrame] = frameFolder;
        _captureFolders[CaptureKind.StreamVideo] = videoRecordingFolder;
        _captureFolders[CaptureKind.StreamAudio] = audioRecordingFolder;
        foreach (var kind in _captureFolders.Keys)
        {
            ShowCaptureFolder(kind);
        }
        VersionText.Text = ProductInfo.Version;
        AuthorText.Text = ProductInfo.Author;

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
    public bool HideAdultContent => HideAdultContentCheckBox.IsChecked == true;
    public bool AnimatedBackdrop => AnimatedBackdropCheckBox.IsChecked == true;
    public bool KeepAwakeDuringPlayback => KeepAwakeCheckBox.IsChecked == true;
    public bool SystemMediaControls => SystemMediaControlsCheckBox.IsChecked == true;
    public bool ResumePlaybackOnStartup => ResumePlaybackCheckBox.IsChecked == true;
    public MediaBackend SelectedVideoBackend => Enum.Parse<MediaBackend>(((UiOption)VideoBackendBox.SelectedItem).Value);

    /// <summary>The chosen frames folder, or <c>null</c> for the frames' default folder at capture time.</summary>
    public string? FrameFolder => _captureFolders[CaptureKind.VideoFrame];

    /// <summary>The chosen video recordings folder, or <c>null</c> for their default folder (SP-0179).</summary>
    public string? VideoRecordingFolder => _captureFolders[CaptureKind.StreamVideo];

    /// <summary>The chosen radio recordings folder, or <c>null</c> for their default folder (SP-0179).</summary>
    public string? AudioRecordingFolder => _captureFolders[CaptureKind.StreamAudio];

    /// <summary>The kind a folder button edits: its <c>Tag</c> names it, so the three rows share one set of handlers.</summary>
    private static CaptureKind KindOf(object sender) =>
        sender is FrameworkElement { Tag: string tag } && Enum.TryParse<CaptureKind>(tag, out var kind) ? kind : CaptureKind.VideoFrame;

    /// <summary>The folder the kind's files go to now: the pending choice, or the kind's default.</summary>
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
            // A default folder the first capture has not created yet opens at its parent - Pictures, Videos, Music.
            InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current) ?? current,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _captureFolders[kind] = dialog.FolderName;
            ShowCaptureFolder(kind);
        }
    }

    private void FrameFolderReset_Click(object sender, RoutedEventArgs e)
    {
        var kind = KindOf(sender);
        _captureFolders[kind] = null;
        ShowCaptureFolder(kind);
    }

    // Navigation only: opening the folder changes nothing, so it may live beside Cancel. It no longer creates
    // a missing folder first - that was a write before Save, and the capture itself creates the folder when
    // it is first needed.
    private void FrameFolderOpen_Click(object sender, RoutedEventArgs e)
    {
        var folder = ResolvedFolder(KindOf(sender));
        if (!LogReportMailer.OpenFolder(folder))
        {
            MessageBox.Show(this, LocalizationService.Format("LogArchiveOpenFolderFailed", folder), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// SP-0026 - states whether the FlyleafLib components are present. The engine ComboBox on its own would
    /// let the user select an engine that cannot start, so this line is what makes the choice above mean
    /// something. Installing and removing them is an operation, so it happens in the Tools window.
    /// </summary>
    private void ShowVideoComponents()
    {
        var installed = FFmpegComponents.IsInstalled(FFmpegComponents.ResolveFolder(AppPaths.DataDirectory));
        VideoComponentsStatusText.Text = installed
            ? LocalizationService.Get("VideoComponentsInstalled")
            : LocalizationService.Format(
                "VideoComponentsMissing", FFmpegComponentsInstaller.ApproximateDownloadMegabytes);
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
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
