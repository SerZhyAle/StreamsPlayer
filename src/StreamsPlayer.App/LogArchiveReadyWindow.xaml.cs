using System.IO;
using System.Windows;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0049: a truthful post-write confirmation. The path remains selectable even if the user closes
/// every other window, and opening its folder is an explicit choice rather than a side effect.
/// </summary>
public partial class LogArchiveReadyWindow : Window
{
    private readonly string _archivePath;

    public LogArchiveReadyWindow(string archivePath)
    {
        InitializeComponent();
        _archivePath = archivePath;
        ArchivePathBox.Text = archivePath;
        Loaded += (_, _) =>
        {
            ArchivePathBox.Focus();
            ArchivePathBox.SelectAll();
        };
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_archivePath);
        if (string.IsNullOrWhiteSpace(folder) || !LogReportMailer.OpenFolder(folder))
        {
            MessageBox.Show(this, LocalizationService.Format("LogArchiveOpenFolderFailed", folder ?? _archivePath), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
