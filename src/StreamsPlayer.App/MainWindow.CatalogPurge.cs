using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0030: one confirmed action that removes every downloaded catalog row, leaving the user with only
// their own manual/imported channels. The removal rule itself lives in Core (CatalogPurge); this file
// only confirms, persists, releases UI state for the rows that left, and reports the outcome.
public partial class MainWindow
{
    private async Task DeleteDownloadedChannelsAsync(Window owner)
    {
        var downloaded = CatalogPurge.CountDownloaded(_state);
        if (downloaded == 0)
        {
            MessageBox.Show(owner, LocalizationService.Get("DeleteDownloadedNone"),
                LocalizationService.Get("DeleteDownloadedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(owner, LocalizationService.Format("DeleteDownloadedConfirm", downloaded),
                LocalizationService.Get("DeleteDownloadedTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        CatalogPurgeResult? purge = null;
        await PersistAsync(state =>
        {
            purge = CatalogPurge.RemoveDownloaded(state);
            var collections = purge.RemovedChannelIds.Aggregate(
                (IReadOnlyList<ChannelCollection>)purge.State.Collections,
                ChannelCollections.RemoveChannelEverywhere);
            return purge.State with { Collections = [.. collections] };
        });
        if (purge is null)
        {
            return;
        }

        foreach (var id in purge.RemovedChannelIds)
        {
            ForgetRow(id);
        }

        _log.Event("CATALOG PURGE", $"removed={purge.RemovedChannelIds.Count}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("DeleteDownloadedResult", purge.RemovedChannelIds.Count);
    }

    private async Task DeleteImportedCatalogAsync(Window owner)
    {
        var count = CatalogPurge.CountImportedBank(_state.Channels);
        if (count == 0)
        {
            MessageBox.Show(owner, LocalizationService.Get("DeleteImportedCatalogNone"),
                LocalizationService.Get("DeleteImportedCatalogTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(owner, LocalizationService.Format("DeleteImportedCatalogConfirm", count),
                LocalizationService.Get("DeleteImportedCatalogTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        CatalogPurgeResult? purge = null;
        await PersistAsync(state =>
        {
            purge = CatalogPurge.RemoveImportedBank(state);
            var collections = purge.RemovedChannelIds.Aggregate(
                (IReadOnlyList<ChannelCollection>)purge.State.Collections,
                ChannelCollections.RemoveChannelEverywhere);
            return purge.State with { Collections = [.. collections] };
        });
        if (purge is null)
        {
            return;
        }

        foreach (var id in purge.RemovedChannelIds)
        {
            ForgetRow(id);
        }

        _log.Event("IMPORTED CATALOG PURGE", $"removed={purge.RemovedChannelIds.Count}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("DeleteImportedCatalogResult", purge.RemovedChannelIds.Count);
    }
}
