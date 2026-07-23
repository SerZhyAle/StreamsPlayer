using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0016: shows the atomic-import preview (new / duplicate / invalid / skipped counts) before any change is
// applied. Apply is enabled only when there is at least one new channel; the caller performs the single save.
public partial class ImportPreviewWindow : Window
{
    public ImportPreviewWindow(string sourceLabel, M3uImportPreview preview)
    {
        InitializeComponent();
        SourceText.Text = sourceLabel;
        NewCountText.Text = preview.NewCount.ToString();
        DuplicateCountText.Text = preview.DuplicateCount.ToString();
        InvalidCountText.Text = preview.InvalidCount.ToString();
        SkippedCountText.Text = preview.SkippedCount.ToString();
        ApplyButton.IsEnabled = preview.NewCount > 0;
        ExplanationText.Text = preview.NewCount > 0
            ? LocalizationService.Format("ImportPreviewSummary", preview.NewCount)
            : LocalizationService.Get("ImportPreviewNothingNew");
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
