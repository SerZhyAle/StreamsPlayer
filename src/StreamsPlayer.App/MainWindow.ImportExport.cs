using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0016: the actions the Tools window delegates to the owning window, which is the one that holds the
// catalog state (SP-0030 added DeleteDownloaded, SP-0040 SendLogsToAuthor). SP-0109 moved them out of the
// Settings window, whose Cancel could not undo any of them, and renamed the set after the window that runs
// it now. The owning window is passed in so every file picker, prompt, preview, and message box is owned by
// whichever window triggered the action.
public enum ToolsAction
{
    ImportFromFile,
    ImportFromUrl,
    ExportAll,
    ExportPinned,
    ManageHidden,
    ApplyCatalogSnapshot,
    DeleteDownloaded,
    ImportCatalogFromFile,
    DeleteImportedCatalog,
    SendLogsToAuthor,
    InstallVideoComponents,
    RemoveVideoComponents,
    DownloadTvSchedule,
    RemoveTvSchedule
}

// SP-0016: M3U import/export portability. Import is additive and atomic - it only ever inserts Imported rows
// and never overwrites or prunes existing rows (so CatalogMerger, which stamps Catalog and prunes, is not
// reused). Export is limited to user-owned (Manual/Imported/LocalCatalog) rows, optionally the pinned subset.
public partial class MainWindow
{
    internal async Task RunToolsActionAsync(ToolsAction action, Window owner)
    {
        if (_toolsActionActive || _busy)
        {
            return;
        }

        _toolsActionActive = true;
        try
        {
            switch (action)
            {
                case ToolsAction.ImportFromFile:
                    await ImportFromFileAsync(owner);
                    break;
                case ToolsAction.ImportFromUrl:
                    await ImportFromUrlAsync(owner);
                    break;
                case ToolsAction.ExportAll:
                    await ExportAsync(pinnedOnly: false, owner);
                    break;
                case ToolsAction.ExportPinned:
                    await ExportAsync(pinnedOnly: true, owner);
                    break;
                case ToolsAction.ManageHidden:
                    await ShowHiddenChannelsAsync(owner);
                    break;
                case ToolsAction.ApplyCatalogSnapshot:
                    await ApplyBundledSnapshotAsync(owner);
                    break;
                case ToolsAction.DeleteDownloaded:
                    await DeleteDownloadedChannelsAsync(owner);
                    break;
                case ToolsAction.ImportCatalogFromFile:
                    await ImportCatalogFromFileAsync(owner);
                    break;
                case ToolsAction.DeleteImportedCatalog:
                    await DeleteImportedCatalogAsync(owner);
                    break;
                case ToolsAction.SendLogsToAuthor:
                    await SendLogsToAuthorAsync(owner);
                    break;
                case ToolsAction.InstallVideoComponents:
                    await InstallVideoComponentsAsync(owner);
                    break;
                case ToolsAction.RemoveVideoComponents:
                    await RemoveVideoComponentsAsync(owner);
                    break;
                case ToolsAction.DownloadTvSchedule:
                    await DownloadTvScheduleAsync(owner);
                    break;
                case ToolsAction.RemoveTvSchedule:
                    await RemoveTvScheduleAsync(owner);
                    break;
            }
        }
        finally
        {
            _toolsActionActive = false;
        }
    }

    private async Task ImportFromFileAsync(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.Get("ImportSourceFileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        if (Path.GetExtension(dialog.FileName).Equals(".fmsbcast", StringComparison.OrdinalIgnoreCase))
        {
            await ImportFastMediaSorterBroadcastFileAsync(dialog.FileName, owner);
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
        if (_busy)
        {
            return;
        }

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
        // SP-0129: TimeoutException (no head, or a silent body) and InvalidDataException (media rather than a
        // playlist, or over the ceiling) are the service's bounded failures; both read as "could not import".
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or DecoderFallbackException or InvalidOperationException or TimeoutException or InvalidDataException)
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

        _state = await PersistAsync(state => state with { Channels = [.. state.Channels, .. additions] });
        _log.Event("IMPORT APPLY", $"count={additions.Count}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("ImportResult", additions.Count);
    }

    private async Task ImportCatalogFromFileAsync(Window owner)
    {
        if (_busy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.Get("ImportCatalogFileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        SetBusy(true);
        SetStatus("CatalogApplying");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        try
        {
            StreamBank bank;
            using (var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > StreamCatalogService.MaximumArchiveBytes)
                {
                    throw new InvalidDataException($"The catalog archive exceeds the maximum limit of {StreamCatalogService.MaximumArchiveBytes} bytes.");
                }

                bank = StreamBankReader.Read(stream);
            }

            if (bank.Entries.Count == 0)
            {
                _log.Event("CATALOG IMPORT REFUSE", "reason=no_channels");
                MessageBox.Show(owner, LocalizationService.Get("ImportCatalogNoChannels"),
                    LocalizationService.Get("ImportCatalogTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var bankCarriedAtlas = bank.FaviconAtlas is { Length: > 0 };
            var entries = bankCarriedAtlas
                ? bank.Entries
                : [.. bank.Entries.Select(entry => entry with { FaviconIndex = null })];

            _log.Event("CATALOG ATLAS", "op=catalog_file_import",
                $"bank_atlas={(bankCarriedAtlas ? "present" : "absent")}",
                $"installed={(bankCarriedAtlas ? "replaced" : "kept")}");

            MergeResult? merge = null;
            _state = await PersistAsync(
                state =>
                {
                    merge = CatalogMerger.Merge(
                        state.Channels,
                        entries,
                        DateTimeOffset.UtcNow,
                        new CatalogMergeOptions(
                            RemoveMissing: false,
                            FaviconSource: FaviconSource.Imported,
                            TargetOrigin: SourceOrigin.LocalCatalog,
                            ReplacesAtlas: bankCarriedAtlas),
                        channelsWithUserData: UserAuthoredChannels.Identify(state));
                    return FaviconAtlasReferences.ReleaseUnreferenced(
                        state with { Channels = merge.Channels.ToList() }, AtlasSlot.Imported);
                },
                (state, cancellationToken) => _store.SaveAsync(
                    state,
                    bank.FaviconAtlas,
                    bankCarriedAtlas,
                    AtlasSlot.Imported,
                    cancellationToken));
            if (merge is null)
            {
                return;
            }

            _log.Event("CATALOG IMPORT APPLY",
                $"added={merge.Added}",
                $"updated={merge.Updated}",
                $"removed={merge.Removed}",
                $"retired={merge.Retired}");

            await PruneCollectionsAsync();
            PopulateFacets();
            ApplyFilter();
            SetStatus("ImportCatalogResult", merge.Added, merge.Updated, merge.Removed);
            if (IsGridMode && _previewCoordinator is not null)
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                await QueueVisibleSafelyAsync(force: true);
            }
        }
        catch (InvalidDataException exception)
        {
            _log.Event("CATALOG IMPORT FAIL", "reason=invalid_data", $"message={exception.Message}");
            var key = exception.Message.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) ||
                      exception.Message.Contains("CSV", StringComparison.OrdinalIgnoreCase)
                ? "ImportCatalogInvalidCsv"
                : exception.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase)
                    ? "ImportCatalogExceededLimit"
                    : "ImportCatalogInvalidArchive";
            SetStatus("ImportFailedStatus");
            MessageBox.Show(owner, LocalizationService.Get(key), LocalizationService.Get("ImportCatalogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Event("CATALOG IMPORT FAIL", "reason=io", $"message={exception.Message}");
            SetStatus("ImportFailedStatus");
            MessageBox.Show(owner, LocalizationService.Get("ImportFileReadFailed"), LocalizationService.Get("ImportCatalogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportAsync(bool pinnedOnly, Window owner)
    {
        var rows = _state.Channels
            .Where(channel => channel.SourceOrigin is SourceOrigin.Manual or SourceOrigin.Imported or SourceOrigin.LocalCatalog)
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
