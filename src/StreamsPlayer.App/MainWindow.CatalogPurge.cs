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
        var downloaded = CatalogPurge.CountDownloaded(_state.Channels);
        if (downloaded == 0)
        {
            MessageBox.Show(owner, LocalizationService.Get("DeleteDownloadedNone"),
                LocalizationService.Get("DeleteDownloadedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(owner, LocalizationService.Format("DeleteDownloadedConfirm", downloaded),
                LocalizationService.Get("DeleteDownloadedTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return;
        }

        var purge = CatalogPurge.RemoveDownloaded(_state);
        // SP-0017: the removed rows also leave every collection; empty collections stay for the user.
        var collections = purge.RemovedChannelIds.Aggregate(
            (IReadOnlyList<ChannelCollection>)purge.State.Collections,
            ChannelCollections.RemoveChannelEverywhere);
        _state = await _store.SaveAsync(purge.State with { Collections = [.. collections] });
        foreach (var id in purge.RemovedChannelIds)
        {
            ForgetRow(id);
        }

        _log.Event("CATALOG PURGE", $"removed={purge.RemovedChannelIds.Count}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("DeleteDownloadedResult", purge.RemovedChannelIds.Count);
    }
}
