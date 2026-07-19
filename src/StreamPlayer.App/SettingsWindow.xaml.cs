using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using StreamPlayer.Core;

namespace StreamPlayer.App;

public partial class SettingsWindow : Window
{
    private readonly AppLanguage _language;
    private readonly StreamChannel? _selectedChannel;

    public SettingsWindow(StreamTileSize tileSize, bool updateStreamPreviews, AppLanguage language, StreamChannel? selectedChannel)
    {
        InitializeComponent();
        _language = language;
        _selectedChannel = selectedChannel;
        var sizes = new[]
        {
            new UiOption(nameof(StreamTileSize.Small), LocalizationService.Get("TileSmall")),
            new UiOption(nameof(StreamTileSize.Medium), LocalizationService.Get("TileMedium")),
            new UiOption(nameof(StreamTileSize.Large), LocalizationService.Get("TileLarge"))
        };
        TileSizeBox.ItemsSource = sizes;
        TileSizeBox.SelectedItem = sizes.First(item => item.Value == tileSize.ToString());
        UpdatePreviewsCheckBox.IsChecked = updateStreamPreviews;
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

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
