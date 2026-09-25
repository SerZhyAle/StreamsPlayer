using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0016: prompts for a remote HTTP(S) playlist URL to import. Validation only checks the address is a
// fetchable http/https URL here; parsing and de-duplication happen in Core after the body is downloaded.
public partial class ImportUrlWindow : Window
{
    public ImportUrlWindow() => InitializeComponent();

    public string PlaylistUrl => UrlBox.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!LaunchableAddress.TryParseHttp(PlaylistUrl, out _))
        {
            MessageBox.Show(this, LocalizationService.Get("ImportUrlInvalid"),
                LocalizationService.Get("InvalidUrlTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
