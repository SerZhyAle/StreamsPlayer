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

    private async Task ImportFastMediaSorterBroadcastAsync(FastMediaSorterBroadcastRead read, Window owner)
    {
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
                FastMediaSorterBroadcastReadStatus.UnsupportedSchema => "FmsBroadcastUnsupportedSchema",
                FastMediaSorterBroadcastReadStatus.UnsupportedMode => "FmsBroadcastUnsupportedMode",
                FastMediaSorterBroadcastReadStatus.TooLarge => "FmsBroadcastTooLarge",
                _ => "FmsBroadcastInvalid"
            };
            _log.Event("FMS IMPORT SKIP", $"status={read.Status}");
            MessageBox.Show(owner, LocalizationService.Get(key), LocalizationService.Get("FmsBroadcastTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var planned = FastMediaSorterBroadcastImport.Apply(_state.Channels, read.Broadcast, DateTimeOffset.UtcNow);
        if (MessageBox.Show(owner,
                LocalizationService.Format("FmsBroadcastConfirm", planned.Channel.Title,
                    CatalogUrlIdentity.Redact(planned.Channel.Url)),
                LocalizationService.Get("FmsBroadcastTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
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
    }

    private void MainWindow_PreviewDragOver(object sender, DragEventArgs e)
    {
        // SP-0158: while busy the import is refused, so advertising Copy here would promise a copy the
        // window will not perform.
        e.Effects = !_busy && TryGetBroadcastFile(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void MainWindow_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (TryGetBroadcastFile(e, out var path))
            {
                await ImportFastMediaSorterBroadcastFileAsync(path, this);
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
            files.Length != 1 || !Path.GetExtension(files[0]).Equals(".fmsbcast", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = files[0];
        return true;
    }
}
