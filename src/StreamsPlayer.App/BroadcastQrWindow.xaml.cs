using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// The link of a running broadcast as a QR code for a phone to scan. The code holds the description the way the link does
/// and never the password; the window shows no text of either, so nothing here can be read off a screen share.
/// </summary>
public partial class BroadcastQrWindow : Window
{
    private const int ModuleScale = 8;

    public BroadcastQrWindow(string descriptorJson)
    {
        InitializeComponent();
        var modules = BroadcastQrCode.Modules(FastMediaSorterBroadcastShare.BarcodeText(descriptorJson));
        if (modules is null)
        {
            QrImage.Visibility = Visibility.Collapsed;
            TooLongText.Visibility = Visibility.Visible;
            return;
        }

        QrImage.Source = BroadcastQrCode.Picture(modules, ModuleScale);
    }
}
