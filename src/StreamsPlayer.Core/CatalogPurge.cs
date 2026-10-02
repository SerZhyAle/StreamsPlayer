namespace StreamsPlayer.Core;

/// <summary>
/// SP-0030 & SP-0098: explicit, user-confirmed removal of catalog rows:
/// - <see cref="RemoveDownloaded"/> removes only <see cref="SourceOrigin.Catalog"/> rows that are not user-authored.
/// - <see cref="RemoveImportedBank"/> removes only <see cref="SourceOrigin.LocalCatalog"/> rows.
/// User-authored content (pinned channels, collection memberships, listening history) is preserved.
/// </summary>
public static class CatalogPurge
{
    public static int CountDownloaded(CatalogState state)
    {
        // SP-0183: only count downloadable channels that are not user-authored
        var authoredIds = UserAuthoredChannels.Identify(state);
        return state.Channels.Count(channel => 
            channel.SourceOrigin == SourceOrigin.Catalog && !authoredIds.Contains(channel.Id));
    }

    public static CatalogPurgeResult RemoveDownloaded(CatalogState state)
    {
        // SP-0183: do not delete user-authored content - channels marked by UserAuthoredChannels
        var authoredIds = UserAuthoredChannels.Identify(state);
        
        var removableChannels = state.Channels
            .Where(channel => channel.SourceOrigin == SourceOrigin.Catalog && !authoredIds.Contains(channel.Id))
            .ToList();
            
        var removedIds = removableChannels.Select(channel => channel.Id).ToList();

        if (removedIds.Count == 0)
        {
            return new CatalogPurgeResult(state, []);
        }

        var kept = state.Channels
            .Where(channel => channel.SourceOrigin != SourceOrigin.Catalog || authoredIds.Contains(channel.Id))
            .ToList();

        // SP-0177: the downloaded rows' bookkeeping leaves with them - the atlases nothing indexes any more
        // (the save sweeps their files), and the download and snapshot dates, which describe no row now and
        // would otherwise keep the provenance line claiming a refresh and the first-run offer away for good.
        return new CatalogPurgeResult(
            FaviconAtlasReferences.ReleaseUnreferenced(state with
            {
                Channels = kept,
                LastCatalogRefreshAt = null,
                AppliedSnapshotDate = null
            }, writtenSlot: null),
            removedIds);
    }

    public static int CountImportedBank(IEnumerable<StreamChannel> channels) =>
        channels.Count(channel => channel.SourceOrigin == SourceOrigin.LocalCatalog);

    public static CatalogPurgeResult RemoveImportedBank(CatalogState state)
    {
        var removedIds = state.Channels
            .Where(channel => channel.SourceOrigin == SourceOrigin.LocalCatalog)
            .Select(channel => channel.Id)
            .ToList();

        if (removedIds.Count == 0)
        {
            return new CatalogPurgeResult(state, []);
        }

        var kept = state.Channels
            .Where(channel => channel.SourceOrigin != SourceOrigin.LocalCatalog)
            .ToList();

        var newState = state with { Channels = kept };
        if (!kept.Any(channel => channel.FaviconSource == FaviconSource.Imported))
        {
            newState = newState with { ImportedAtlasFileName = null };
        }

        return new CatalogPurgeResult(newState, removedIds);
    }
}
