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
    private readonly Func<StreamListAction, Window, Task> _runStreamListAction;
    // SP-0038: null means "unset", which resolves to Downloads at save time. The text box always shows a
    // real path so the user can see where frames land either way, hence the separate field.
    private string? _frameFolder;

    public SettingsWindow(StreamTileSize tileSize, bool updateStreamPreviews, bool keepAwakeDuringPlayback, bool systemMediaControls, MediaBackend videoBackend, string? frameFolder, AppLanguage language, StreamChannel? selectedChannel, Func<StreamListAction, Window, Task> runStreamListAction)
    {
        InitializeComponent();
        _language = language;
        _selectedChannel = selectedChannel;
        _runStreamListAction = runStreamListAction;
        var sizes = new[]
        {
            new UiOption(nameof(StreamTileSize.Small), LocalizationService.Get("TileSmall")),
            new UiOption(nameof(StreamTileSize.Medium), LocalizationService.Get("TileMedium")),
            new UiOption(nameof(StreamTileSize.Large), LocalizationService.Get("TileLarge"))
        };
        TileSizeBox.ItemsSource = sizes;
        TileSizeBox.SelectedItem = sizes.First(item => item.Value == tileSize.ToString());
        UpdatePreviewsCheckBox.IsChecked = updateStreamPreviews;
        KeepAwakeCheckBox.IsChecked = keepAwakeDuringPlayback;
        SystemMediaControlsCheckBox.IsChecked = systemMediaControls;
        var backends = new[]
        {
            new UiOption(nameof(MediaBackend.LibVlc), LocalizationService.Get("VideoBackendLibVlc")),
            new UiOption(nameof(MediaBackend.Flyleaf), LocalizationService.Get("VideoBackendFlyleaf"))
        };
        VideoBackendBox.ItemsSource = backends;
        VideoBackendBox.SelectedItem = backends.First(item => item.Value == videoBackend.ToString());
        _frameFolder = frameFolder;
        ShowFrameFolder();
        VersionText.Text = ProductInfo.Version;
        AuthorText.Text = ProductInfo.Author;
        SelectedStreamText.Text = selectedChannel is null
            ? LocalizationService.Get("NoStreamSelected")
            : StreamTitleFormatter.Display(selectedChannel.Title);
        CopyLaunchCommandButton.IsEnabled = selectedChannel is not null;
        CreateDesktopShortcutButton.IsEnabled = selectedChannel is not null;
    }

    public StreamTileSize SelectedTileSize => Enum.Parse<StreamTileSize>(((UiOption)TileSizeBox.SelectedItem).Value);
    public bool UpdateStreamPreviews => UpdatePreviewsCheckBox.IsChecked == true;
    public bool KeepAwakeDuringPlayback => KeepAwakeCheckBox.IsChecked == true;
    public bool SystemMediaControls => SystemMediaControlsCheckBox.IsChecked == true;
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

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void ImportFromFile_Click(object sender, RoutedEventArgs e) =>
        await _runStreamListAction(StreamListAction.ImportFromFile, this);

    private async void ImportFromUrl_Click(object sender, RoutedEventArgs e) =>
        await _runStreamListAction(StreamListAction.ImportFromUrl, this);

    private async void ExportAll_Click(object sender, RoutedEventArgs e) =>
        await _runStreamListAction(StreamListAction.ExportAll, this);

    private async void ExportPinned_Click(object sender, RoutedEventArgs e) =>
        await _runStreamListAction(StreamListAction.ExportPinned, this);

    // Immediate like the delete below: unhiding a channel commits on its own, so Cancel here does not
    // undo it.
    private async void ManageHidden_Click(object sender, RoutedEventArgs e) =>
        await _runStreamListAction(StreamListAction.ManageHidden, this);

    // SP-0030: destructive and immediate - the confirmation inside the action is the commit point,
    // so closing Settings with Cancel does not bring the downloaded rows back.
    private async void DeleteDownloaded_Click(object sender, RoutedEventArgs e) =>
        await _runStreamListAction(StreamListAction.DeleteDownloaded, this);

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
