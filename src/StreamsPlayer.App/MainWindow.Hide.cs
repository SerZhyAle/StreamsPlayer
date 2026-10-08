using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0020: origin-aware removal of a stream from the failure dialog and hidden-row exclusion from views.
// Bank rows are hidden (durable across explicit refresh and import); user-owned rows are deleted.
public partial class MainWindow
{
    /// <summary>Normalized identities of the currently hidden catalog channels; empty when nothing is hidden.</summary>
    private HashSet<string> BuildHiddenIdentitySet() =>
        _state.HiddenCatalogUrls.Count == 0
            ? []
            : new HashSet<string>(_state.HiddenCatalogUrls.Select(CatalogUrlIdentity.Normalize), StringComparer.Ordinal);

    private static bool IsHiddenBySet(HashSet<string> hiddenIdentities, StreamChannel channel) =>
        ChannelOwnership.IsHidden(hiddenIdentities, channel);

    /// <summary>
    /// User-confirmed removal. Bank rows - published or imported (SP-0126, SP-0177) - are hidden, so the next
    /// refresh or import cannot bring them back without their pin and collections; Manual/Imported rows are
    /// deleted.
    /// </summary>
    private Task RemoveChannelAsync(StreamChannel channel) =>
        ChannelOwnership.IsBankSourced(channel.SourceOrigin)
            ? HideCatalogChannelAsync(channel)
            : DeleteUserChannelAsync(channel);

    private async Task HideCatalogChannelAsync(StreamChannel channel)
    {
        if (!ChannelOwnership.IsBankSourced(channel.SourceOrigin) ||
            CatalogUrlIdentity.IsHidden(_state.HiddenCatalogUrls, channel.Url))
        {
            return;
        }

        if (!await TryPersistAsync(state => state with { HiddenCatalogUrls = [.. state.HiddenCatalogUrls, channel.Url] }))
        {
            return;
        }

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
        // SP-0017: the same save drops its collection memberships; the collections themselves stay.
        if (!await TryPersistAsync(state => state with
            {
                Channels = state.Channels.Where(item => item.Id != channel.Id).ToList(),
                Collections = [.. ChannelCollections.RemoveChannelEverywhere(state.Collections, channel.Id)]
            }))
        {
            return;
        }

        ForgetRow(channel.Id);
        _log.Event("CHANNEL DELETE", $"url={channel.Url}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("DeletedStream", StreamTitleFormatter.Display(channel.Title));
    }

    /// <summary>
    /// Opens the hidden-channel manager. Reached from Settings rather than the main header: a hidden
    /// channel is managed rarely, and its button only ever appeared once something was hidden - a
    /// control that comes and goes in the header is harder to find than one that always sits in the
    /// same place. Unhiding commits immediately, so cancelling Settings does not re-hide anything.
    /// </summary>
    private Task ShowHiddenChannelsAsync(Window owner)
    {
        var hiddenIdentities = BuildHiddenIdentitySet();
        var rows = _state.Channels
            .Where(channel => IsHiddenBySet(hiddenIdentities, channel))
            .Select(channel => new HiddenChannelView(
                StreamTitleFormatter.Display(channel.Title),
                CatalogUrlIdentity.Redact(channel.Url),
                channel.Url))
            .ToList();
        var window = new HiddenChannelsWindow(rows, UnhideAsync) { Owner = owner };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    /// <summary>Restore a hidden catalog channel. Only the hidden set changes; the channel record is untouched.</summary>
    private async Task UnhideAsync(string url)
    {
        if (!await TryPersistAsync(state => state with
            {
                HiddenCatalogUrls = state.HiddenCatalogUrls.Where(hidden => !CatalogUrlIdentity.SameIdentity(hidden, url)).ToList()
            }))
        {
            return;
        }

        _log.Event("CHANNEL UNHIDE", $"url={url}");
        PopulateFacets();
        ApplyFilter();
    }

    /// <summary>
    /// Commits one mutation and reports whether it reached the disk. <c>CommitStateAsync</c>
    /// has already logged the failure and queued the "state not saved" status, so a caller only has to stop:
    /// carrying on would forget a row, or report a success, for a change that did not persist (SP-0184 A11-3).
    /// </summary>
    private async Task<bool> TryPersistAsync(Func<CatalogState, CatalogState> mutation)
    {
        var commit = await CommitStateAsync(
            mutation, (state, cancellationToken) => _store.SaveAsync(state, cancellationToken: cancellationToken));
        return commit.Saved;
    }

    /// <summary>Drop cached UI state for a channel that is leaving the visible set.</summary>
    /// <remarks>
    /// SP-0069: unindexing is not optional here. <see cref="PruneRowCache"/> is the only other place a
    /// row leaves the cache and it pairs the two dictionaries; dropping the row from <c>_rowCache</c>
    /// alone strands it in <c>_rowsByUrl</c> with its decoded favicon, and nothing can reach it again -
    /// the prune walks <c>_rowCache</c>, where the entry no longer is. Worse than one orphan per channel:
    /// hiding a channel again after unhiding it builds a *new* row, and <see cref="IndexUrl"/> appends it
    /// under a reference check, so the list grows once per gesture rather than once per channel.
    /// </remarks>
    private void ForgetRow(Guid id)
    {
        if (_rowCache.Remove(id, out var row))
        {
            UnindexUrl(row.Channel.Url, row);
            _tvScheduleLines.Forget(row);
        }

        if (_selectedRow?.Channel.Id == id)
        {
            _selectedRow = null;
        }

        // SP-0184 S11-3: the stop funnel also drops a paused station, so a deleted row cannot stay resumable.
        if (_playingAudio?.Channel.Id == id || _audioPausedChannelId == id)
        {
            StopAudioPlayback();
        }
    }
}
