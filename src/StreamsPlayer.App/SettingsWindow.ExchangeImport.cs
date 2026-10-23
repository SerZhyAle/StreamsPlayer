using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class SettingsWindow
{
    private ExchangeConnectionString? _importedExchange;
    private bool _fillingExchange;

    internal void FillExchangeConnection(ExchangeConnectionString data)
    {
        _fillingExchange = true;
        try
        {
            ExchangeSecretBox.Clear();
            ExchangeAddressBox.Text = data.Host;
            ExchangePortBox.Text = data.Port.ToString(CultureInfo.InvariantCulture);
            ExchangeLoginBox.Text = data.Login ?? "";
            _importedExchange = data;
            // The fingerprint is for whoever opens the details; the face carries the name and the trust line.
            ExchangeImportedPinText.Text = data.Fingerprint ?? "";
            ShowExchangePrompt();
            ExchangeStringEntry.Visibility = Visibility.Collapsed;
            ExchangeStringBox.Clear();
            void Reveal() => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                () => PageScroll.ScrollToVerticalOffset(Math.Max(0, ScrollOffsetOf(ExchangeGroup) - 8)));
            if (IsLoaded) Reveal();
            else Loaded += (_, _) => Reveal();
        }
        finally
        {
            _fillingExchange = false;
        }
    }

    private void ExchangeEndpoint_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_fillingExchange || _importedExchange is null) return;
        // An imported pin belongs to the displayed endpoint. Editing it returns to the manual trust path.
        _importedExchange = null;
        ExchangePreview.Visibility = Visibility.Collapsed;
        ExchangeSecretBox.Clear();
    }

    private void ExchangeImportString_Click(object sender, RoutedEventArgs e)
    {
        ExchangeStringEntry.Visibility = Visibility.Visible;
        try
        {
            ExchangeStringBox.Text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (Exception exception) when (exception is COMException or System.Threading.ThreadStateException)
        {
            MessageBox.Show(this, LocalizationService.Get("PasteClipboardFailed"), LocalizationService.Get("ExchangeTitle"));
        }
        ExchangeStringBox.Focus();
        ExchangeStringBox.SelectAll();
    }

    private void ExchangeReadString_Click(object sender, RoutedEventArgs e) =>
        ApplyExchangeImport(ExchangeConnectionImport.ReadText(ExchangeStringBox.Text));

    private void ExchangeImportFile_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeImportFile_Click), () => RunExchangeActionAsync(async () =>
        {
            var dialog = new OpenFileDialog { Filter = LocalizationService.Get("ExchangeStringFileFilter"),
                Title = LocalizationService.Get("ExchangeImportFile") };
            if (dialog.ShowDialog(this) != true) return;
            ApplyExchangeImport(await ExchangeConnectionImport.ReadFileAsync(dialog.FileName));
        }));

    /// <summary>
    /// SP-0221 requirement 2: a QR is read from a picture file (a saved image, a screenshot file) or from an image on
    /// the clipboard (a screenshot taken to it). A QR carries the connection string byte for byte, so what it holds
    /// goes through the same codec, fills the same form and stops: reading is never connecting (item BN).
    /// </summary>
    private void ExchangeImportQrFile_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeImportQrFile_Click), () => RunExchangeActionAsync(async () =>
        {
            var dialog = new OpenFileDialog { Filter = LocalizationService.Get("ExchangeQrFileFilter"),
                Title = LocalizationService.Get("ExchangeImportQrFile") };
            if (dialog.ShowDialog(this) != true) return;
            ApplyExchangeImport(ExchangeConnectionImport.ReadQr(await ExchangeQrReader.ReadFileAsync(dialog.FileName)));
        }));

    private void ExchangeImportQrClipboard_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeImportQrClipboard_Click), () => RunExchangeActionAsync(async () =>
        {
            ExchangePixels? pixels;
            try
            {
                if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
                {
                    MessageBox.Show(this, LocalizationService.Get("ExchangeQrClipboardEmpty"),
                        LocalizationService.Get("ExchangeTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // The clipboard's bitmap belongs to this thread; its pixels are copied out here and decoded elsewhere.
                pixels = ExchangeQrReader.ToPixels(image);
            }
            catch (Exception exception) when (exception is COMException or System.Threading.ThreadStateException)
            {
                MessageBox.Show(this, LocalizationService.Get("PasteClipboardFailed"), LocalizationService.Get("ExchangeTitle"));
                return;
            }

            var outcome = pixels is null ? new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable)
                : await ExchangeQrReader.ReadAsync(pixels);
            ApplyExchangeImport(ExchangeConnectionImport.ReadQr(outcome));
        }));

    private void ApplyExchangeImport(ExchangeImportResult result)
    {
        if (result.Data is { } data)
            FillExchangeConnection(data);
        else
        {
            ExchangeSecretBox.Clear();
            MessageBox.Show(this, LocalizationService.Get(result.MessageKey!), LocalizationService.Get("ExchangeTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExchangeCancel_Click(object sender, RoutedEventArgs e) => _exchangeCancellation?.Cancel();
}
