using System.IO;
using System.Windows;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>SP-0099's explicit hand-off paths. Parsing and replacement live in Core; this owns Windows I/O and consent.</summary>
public partial class MainWindow
{
    private async Task ImportFastMediaSorterBroadcastFileAsync(string path, Window owner)
    {
        byte[] bytes;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
            if (stream.Length > FastMediaSorterBroadcastDescriptor.MaximumPayloadBytes)
            {
                await ImportFastMediaSorterBroadcastAsync(
                    new FastMediaSorterBroadcastRead(FastMediaSorterBroadcastReadStatus.TooLarge), owner);
                return;
            }

            bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset));
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Event("FMS IMPORT FAIL", "source=file", $"reason={exception.GetType().Name}");
            MessageBox.Show(owner, LocalizationService.Get("ImportFileReadFailed"),
                LocalizationService.Get("FmsBroadcastTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ImportFastMediaSorterBroadcastAsync(FastMediaSorterBroadcastDescriptor.Read(bytes), owner);
    }

    /// <param name="openAfterImport">
    /// SP-0222 requirement 4: the Paste button is the explicit act of opening a pasted link, so after the person
    /// confirms the import the broadcast continues into a window of its own - the same door the broadcast list's
    /// field uses, with the password prompt when the broadcast asks for one. A file or a drop only imports.
    /// </param>
    private async Task ImportFastMediaSorterBroadcastAsync(FastMediaSorterBroadcastRead read, Window owner, bool openAfterImport = false)
    {
        RememberLinkKeys(read.Broadcast);
        if (_busy)
        {
            // SP-0158: the window still accepts the offer while another operation owns it - saying
            // nothing here is the drop that silently does nothing.
            _log.Event("REFUSE", "op=fms_import", "reason=busy");
            MessageBox.Show(owner, LocalizationService.Get("FmsBroadcastBusy"),
                LocalizationService.Get("FmsBroadcastTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!read.IsAccepted || read.Broadcast is null)
        {
            var key = read.Status switch
            {
                // A higher schema is the family's own row, never "update the program" (LIVE-BROADCAST, amendment 2026-10-17).
                FastMediaSorterBroadcastReadStatus.UnsupportedSchema => ExchangeFailureRows.FormatUnsupported.MessageKey,
                FastMediaSorterBroadcastReadStatus.UnsupportedMode => "FmsBroadcastUnsupportedMode",
                FastMediaSorterBroadcastReadStatus.TooLarge => "FmsBroadcastTooLarge",
                _ => "FmsBroadcastInvalid"
            };
            _log.Event("FMS IMPORT SKIP", $"status={read.Status}", "contract=LIVE-BROADCAST");
            MessageBox.Show(owner, LocalizationService.Get(key), LocalizationService.Get("FmsBroadcastTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var planned = FastMediaSorterBroadcastImport.Apply(_state.Channels, read.Broadcast, DateTimeOffset.UtcNow);
        if (MessageBox.Show(owner,
                LocalizationService.Format("FmsBroadcastConfirm", planned.Channel.Title,
                    CatalogUrlIdentity.Redact(planned.Channel.Url)),
                LocalizationService.Get("FmsBroadcastTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        FastMediaSorterBroadcastApplyResult? applied = null;
        await PersistAsync(state =>
        {
            applied = FastMediaSorterBroadcastImport.Apply(state.Channels, read.Broadcast, DateTimeOffset.UtcNow);
            return state with { Channels = [.. applied.Channels] };
        });
        if (applied is null)
        {
            return;
        }

        _log.Event("FMS IMPORT APPLY", $"added={applied.Added}",
            $"url={CatalogUrlIdentity.Redact(applied.Channel.Url)}");
        PopulateFacets();
        ApplyFilter();
        SetStatus(applied.Added ? "FmsBroadcastAdded" : "FmsBroadcastUpdated", applied.Channel.Title);
        await RevealChannelAsync(applied.Channel.Id);
        if (openAfterImport)
        {
            var imported = applied.Channel;
            var opened = await OpenIncomingBroadcastAsync(
                () => Task.FromResult<StreamChannel?>(imported), recordCredentials: null, openedByLink: true);
            if (opened.Outcome == BroadcastOpenOutcome.BoundReached)
            {
                SetStatus(IncomingBroadcastFailure.BoundReachedKey, IncomingBroadcastWindows.Default.Maximum);
            }
        }
    }

    private void MainWindow_PreviewDragOver(object sender, DragEventArgs e)
    {
        // SP-0158: while busy the import is refused, so advertising Copy here would promise a copy the
        // window will not perform.
        if (!TryGetExchangeDrop(e, out _, out _) && !TryGetBroadcastFile(e, out _))
        {
            // Not a drag this window accepts: advertise nothing, but leave the event unhandled so a control
            // with a drop of its own (a text box taking dropped text) still gets to answer it.
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = _busy ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void MainWindow_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (TryGetExchangeDrop(e, out var text, out var exchangePath))
            {
                e.Handled = true;
                var result = text is not null ? ExchangeConnectionImport.ReadText(text)
                    : await ExchangeConnectionImport.ReadFileAsync(exchangePath!);
                // Finish the OLE drop before opening a modal window; the source needs its drag loop back.
                _ = Dispatcher.BeginInvoke(() => OpenExchangeImport(result));
                return;
            }
            if (TryGetBroadcastFile(e, out var path))
            {
                e.Handled = true;
                if (Path.GetExtension(path).Equals(".spres", StringComparison.OrdinalIgnoreCase))
                {
                    await ImportBroadcastResourceFileAsync(path, this);
                }
                else
                {
                    await ImportFastMediaSorterBroadcastFileAsync(path, this);
                }
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MainWindow_Drop), exception);
        }
    }

    private static bool TryGetBroadcastFile(DragEventArgs e, out string path)
    {
        path = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length != 1)
        {
            return false;
        }

        // .fmsbcast is a live-broadcast descriptor (SP-0099); .spres is a broadcast resource document
        // (196 section 6.2) - one drag-in window accepts both, dispatched by extension.
        var extension = Path.GetExtension(files[0]);
        if (!extension.Equals(".fmsbcast", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".spres", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = files[0];
        return true;
    }
}
