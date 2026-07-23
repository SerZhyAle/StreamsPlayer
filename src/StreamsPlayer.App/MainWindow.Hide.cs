using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0020: origin-aware removal of a stream from the failure dialog and hidden-row exclusion from views.
// Catalog rows are hidden (durable across explicit refresh); user-owned rows are deleted.
public partial class MainWindow
{
    /// <summary>Normalized identities of the currently hidden catalog channels; empty when nothing is hidden.</summary>
    private HashSet<string> BuildHiddenIdentitySet() =>
        _state.HiddenCatalogUrls.Count == 0
            ? []
            : new HashSet<string>(_state.HiddenCatalogUrls.Select(CatalogUrlIdentity.Normalize), StringComparer.Ordinal);

    private static bool IsHiddenBySet(HashSet<string> hiddenIdentities, StreamChannel channel) =>
        hiddenIdentities.Count > 0 &&
        channel.SourceOrigin == SourceOrigin.Catalog &&
        hiddenIdentities.Contains(CatalogUrlIdentity.Normalize(channel.Url));

    /// <summary>User-confirmed removal. Catalog rows are hidden; Manual/Imported rows are deleted.</summary>
    private Task RemoveChannelAsync(StreamChannel channel) =>
        channel.SourceOrigin == SourceOrigin.Catalog
            ? HideCatalogChannelAsync(channel)
            : DeleteUserChannelAsync(channel);

    private async Task HideCatalogChannelAsync(StreamChannel channel)
    {
        if (channel.SourceOrigin != SourceOrigin.Catalog ||
            CatalogUrlIdentity.IsHidden(_state.HiddenCatalogUrls, channel.Url))
        {
            return;
        }

        _state = await _store.SaveAsync(_state with { HiddenCatalogUrls = [.. _state.HiddenCatalogUrls, channel.Url] });
        ForgetRow(channel.Id);
        _log.Event("CHANNEL HIDE", $"url={channel.Url}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("HiddenStream", StreamTitleFormatter.Display(channel.Title));
    }

    private async Task DeleteUserChannelAsync(StreamChannel channel)
    {
        if (channel.SourceOrigin is not (SourceOrigin.Manual or SourceOrigin.Imported))
        {
            return;
        }

        // Rebuild the list without this row, matching strictly by Id so a colliding-URL row is never touched.
        _state = await _store.SaveAsync(_state with { Channels = _state.Channels.Where(item => item.Id != channel.Id).ToList() });
        ForgetRow(channel.Id);
        _log.Event("CHANNEL DELETE", $"url={channel.Url}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("DeletedStream", StreamTitleFormatter.Display(channel.Title));
    }

    private void HiddenChannelsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var hiddenIdentities = BuildHiddenIdentitySet();
        var rows = _state.Channels
            .Where(channel => channel.SourceOrigin == SourceOrigin.Catalog && IsHiddenBySet(hiddenIdentities, channel))
            .Select(channel => new HiddenChannelView(
                StreamTitleFormatter.Display(channel.Title),
                CatalogUrlIdentity.Redact(channel.Url),
                channel.Url))
            .ToList();
        var window = new HiddenChannelsWindow(rows, UnhideAsync) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>Restore a hidden catalog channel. Only the hidden set changes; the channel record is untouched.</summary>
    private async Task UnhideAsync(string url)
    {
        _state = await _store.SaveAsync(_state with
        {
            HiddenCatalogUrls = _state.HiddenCatalogUrls.Where(hidden => !CatalogUrlIdentity.SameIdentity(hidden, url)).ToList()
        });
        _log.Event("CHANNEL UNHIDE", $"url={url}");
        PopulateFacets();
        ApplyFilter();
    }

    /// <summary>Drop cached UI state for a channel that is leaving the visible set.</summary>
    private void ForgetRow(Guid id)
    {
        _rowCache.Remove(id);
        if (_selectedRow?.Channel.Id == id)
        {
            _selectedRow = null;
        }

        if (_playingAudio?.Channel.Id == id)
        {
            StopAudioPlayback();
        }
    }
}
