using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace StreamsPlayer.App;

internal static class ExchangeHelp
{
    internal static void Open(Window owner, ExchangePortalPage page)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProductInfo.ExchangePortalUrl(LocalizationService.CurrentLanguage, page))
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, LocalizationService.Get("SettingsOpenLinkFailed"),
                LocalizationService.Get("SettingsOpenLinkFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
