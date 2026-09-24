using System.ComponentModel;
using System.Diagnostics;
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
    // SP-0038: null means "unset", which resolves to Downloads at save time. The text box always shows a
    // real path so the user can see where frames land either way, hence the separate field.
    private string? _frameFolder;

    public SettingsWindow(AppTheme theme, StreamTileSize tileSize, bool updateStreamPreviews, bool hideAdultContent, bool animatedBackdrop, bool keepAwakeDuringPlayback, bool systemMediaControls, bool resumePlaybackOnStartup, MediaBackend videoBackend, string? frameFolder, AppLanguage language)
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
        _frameFolder = frameFolder;
        ShowFrameFolder();
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

    // Navigation only: opening the folder changes nothing, so it may live beside Cancel. It no longer creates
    // a missing folder first - that was a write before Save, and the capture itself creates the folder when
    // it is first needed.
    private void FrameFolderOpen_Click(object sender, RoutedEventArgs e)
    {
        var folder = CapturedFrameWriter.ResolveFolder(_frameFolder);
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
                MessageBoxImage.Warning) != MessageBoxResult.OK)
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
