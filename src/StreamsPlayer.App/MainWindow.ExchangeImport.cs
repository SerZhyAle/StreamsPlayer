using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private void MainWindow_ExchangePasteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        try
        {
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (OutOfMemoryException)
            {
                // S12-9: a malformed clipboard payload reaches a read as OutOfMemoryException, not as a COM error
                // (MainWindow.ChannelShare.cs). This handler sees every Ctrl+V in the window, the search box
                // included, so that is "no usable text" and left to the focused control - never the end of the process.
                text = "";
            }

            if (!ExchangeConnectionImport.Recognizes(text))
            {
                // A screenshot of the QR is on the clipboard as an image. It is recognised by what it holds, like
                // the text: a picture with no exchange string in it is some other paste's business and is left alone.
                if (Clipboard.ContainsImage())
                {
                    HandlerBoundary.Run(nameof(ReadExchangeQrFromClipboardAsync), ReadExchangeQrFromClipboardAsync);
                }

                return;
            }

            e.Handled = true;
            OpenExchangeImport(ExchangeConnectionImport.ReadText(text));
        }
        catch (Exception exception) when (exception is COMException or System.Threading.ThreadStateException)
        {
            e.Handled = true;
            MessageBox.Show(this, LocalizationService.Get("PasteClipboardFailed"), LocalizationService.Get("ExchangeTitle"));
        }
    }

    private async Task ReadExchangeQrFromClipboardAsync()
    {
        ExchangePixels? pixels;
        try
        {
            pixels = Clipboard.GetImage() is { } image ? ExchangeQrReader.ToPixels(image) : null;
        }
        catch (Exception exception) when (exception is COMException or System.Threading.ThreadStateException)
        {
            // Nobody asked for this read: a busy clipboard is not worth a message.
            return;
        }

        if (pixels is null)
        {
            return;
        }

        var outcome = await ExchangeQrReader.ReadAsync(pixels);
        if (outcome.Kind == StreamsPlayer.Core.ExchangeQrOutcomeKind.Found && ExchangeConnectionImport.Recognizes(outcome.Text ?? ""))
        {
            OpenExchangeImport(ExchangeConnectionImport.ReadQr(outcome));
        }
    }

    private void OpenExchangeImport(ExchangeImportResult result)
    {
        if (_busy)
        {
            MessageBox.Show(this, LocalizationService.Get("SettingsWindowBusyClose"), LocalizationService.Get("ExchangeTitle"));
            return;
        }
        if (result.Data is { } data)
            OpenSettings(SettingsWindow.ExchangePageIndex, data);
        else
            MessageBox.Show(this, LocalizationService.Get(result.MessageKey!), LocalizationService.Get("ExchangeTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool TryGetExchangeDrop(DragEventArgs e, out string? text, out string? path)
    {
        text = path = null;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files.Length == 1 && (ExchangeConnectionImport.RecognizesFile(files[0])
                || Path.GetExtension(files[0]).Equals(".fmsexch", StringComparison.OrdinalIgnoreCase)))
        {
            path = files[0];
            return true;
        }
        if (e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string value
            && ExchangeConnectionImport.Recognizes(value))
        {
            text = value;
            return true;
        }
        return false;
    }
}
