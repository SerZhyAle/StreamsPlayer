using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0016: the four M3U import/export actions the Settings window exposes. The owning window is passed in so
// every file picker, prompt, preview, and message box is owned by whichever window triggered the action.
public enum StreamListPortabilityAction
{
    ImportFromFile,
    ImportFromUrl,
    ExportAll,
    ExportPinned
}

// SP-0016: M3U import/export portability. Import is additive and atomic — it only ever inserts Imported rows
// and never overwrites or prunes existing rows (so CatalogMerger, which stamps Catalog and prunes, is not
// reused). Export is limited to user-owned (Manual/Imported) rows, optionally the pinned subset.
public partial class MainWindow
{
    internal Task RunStreamListPortabilityAsync(StreamListPortabilityAction action, Window owner) => action switch
    {
        StreamListPortabilityAction.ImportFromFile => ImportFromFileAsync(owner),
        StreamListPortabilityAction.ImportFromUrl => ImportFromUrlAsync(owner),
        StreamListPortabilityAction.ExportAll => ExportAsync(pinnedOnly: false, owner),
        StreamListPortabilityAction.ExportPinned => ExportAsync(pinnedOnly: true, owner),
        _ => Task.CompletedTask
    };

    private async Task ImportFromFileAsync(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.Get("ImportFileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        string text;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            text = M3uImportService.DecodeUtf8(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            _log.Event("IMPORT FAIL", "source=file", $"reason={exception.GetType().Name}");
            var key = exception is DecoderFallbackException ? "ImportInvalidEncoding" : "ImportFileReadFailed";
            MessageBox.Show(owner, LocalizationService.Get(key), LocalizationService.Get("ImportListPlain"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ShowPreviewAndApplyAsync(Path.GetFileName(dialog.FileName), text, owner);
    }

    private async Task ImportFromUrlAsync(Window owner)
    {
        var prompt = new ImportUrlWindow { Owner = owner };
        if (prompt.ShowDialog() != true)
        {
            return;
        }

        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _log.Event("REFUSE", "op=import_url", "reason=offline");
            MessageBox.Show(owner, LocalizationService.Get("OfflineCatalog"), LocalizationService.Get("ImportListPlain"));
            return;
        }

        string text;
        SetStatus("ImportDownloading");
        SetBusy(true);
        try
        {
            var service = new M3uImportService(_httpClient);
            text = await service.FetchAsync(prompt.PlaylistUrl);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or DecoderFallbackException or InvalidOperationException)
        {
            _log.Event("IMPORT FAIL", "source=url", $"reason={exception.GetType().Name}");
            var key = exception is DecoderFallbackException ? "ImportInvalidEncoding" : "ImportUrlFailed";
            SetStatus("ImportFailedStatus");
            MessageBox.Show(owner, LocalizationService.Get(key), LocalizationService.Get("ImportListPlain"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await ShowPreviewAndApplyAsync(CatalogUrlIdentity.Redact(prompt.PlaylistUrl), text, owner);
    }

    private async Task ShowPreviewAndApplyAsync(string sourceLabel, string text, Window owner)
    {
        var existing = new HashSet<string>(_state.Channels.Select(channel => channel.Url), StringComparer.Ordinal);
        var preview = M3uPlaylistParser.Analyze(text, existing);

        if (preview.Status != M3uImportStatus.Ok)
        {
            var key = preview.Status == M3uImportStatus.HlsManifest ? "ImportHlsManifest" : "ImportEmpty";
            _log.Event("IMPORT SKIP", $"status={preview.Status}");
            MessageBox.Show(owner, LocalizationService.Get(key), LocalizationService.Get("ImportListPlain"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new ImportPreviewWindow(sourceLabel, preview) { Owner = owner };
        if (window.ShowDialog() != true)
        {
            return;
        }

        await ApplyImportAsync(preview);
    }

    private async Task ApplyImportAsync(M3uImportPreview preview)
    {
        var now = DateTimeOffset.UtcNow;
        var nextOrder = _state.Channels.Count == 0 ? 0 : _state.Channels.Max(channel => channel.SortIndex) + 1;
        var additions = preview.NewEntries.Select((entry, offset) => new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = entry.Url,
            Title = entry.Title,
            MediaKind = entry.MediaKind,
            SourceOrigin = SourceOrigin.Imported,
            SortIndex = nextOrder + offset,
            AddedAt = now
        }).ToList();

        _state = await _store.SaveAsync(_state with { Channels = [.. _state.Channels, .. additions] });
        _log.Event("IMPORT APPLY", $"count={additions.Count}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("ImportResult", additions.Count);
    }

    private async Task ExportAsync(bool pinnedOnly, Window owner)
    {
        var rows = _state.Channels
            .Where(channel => channel.SourceOrigin is SourceOrigin.Manual or SourceOrigin.Imported)
            .Where(channel => !pinnedOnly || channel.Pinned)
            .OrderBy(channel => channel.SortIndex)
            .ToList();

        if (rows.Count == 0)
        {
            MessageBox.Show(owner, LocalizationService.Get(pinnedOnly ? "ExportNoPinned" : "ExportNoUserRows"),
                LocalizationService.Get("ExportListPlain"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (rows.Any(channel => CatalogUrlIdentity.HasCredentials(channel.Url)) &&
            MessageBox.Show(owner, LocalizationService.Get("ExportCredentialWarning"),
                LocalizationService.Get("ExportListPlain"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = LocalizationService.Get("ImportFileFilter"),
            FileName = "streamsplayer.m3u",
            DefaultExt = ".m3u",
            AddExtension = true
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            var body = M3uPlaylistWriter.Write(rows);
            await File.WriteAllTextAsync(dialog.FileName, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _log.Event("EXPORT", $"count={rows.Count}", $"pinnedOnly={pinnedOnly}");
            SetStatus("ExportResult", rows.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Event("EXPORT FAIL", exception.GetType().Name);
            MessageBox.Show(owner, LocalizationService.Get("ExportFailed"), LocalizationService.Get("ExportListPlain"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
