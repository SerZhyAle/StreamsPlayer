namespace StreamsPlayer.Core;

/// <summary>
/// SP-0030 & SP-0098: explicit, user-confirmed removal of catalog rows:
/// - <see cref="RemoveDownloaded"/> removes only <see cref="SourceOrigin.Catalog"/> rows.
/// - <see cref="RemoveImportedBank"/> removes only <see cref="SourceOrigin.LocalCatalog"/> rows.
/// Neither touches user-authored rows (Manual/Imported).
/// </summary>
public static class CatalogPurge
{
    public static int CountDownloaded(IEnumerable<StreamChannel> channels) =>
        channels.Count(channel => channel.SourceOrigin == SourceOrigin.Catalog);

    public static CatalogPurgeResult RemoveDownloaded(CatalogState state)
    {
        var removedIds = state.Channels
            .Where(channel => channel.SourceOrigin == SourceOrigin.Catalog)
            .Select(channel => channel.Id)
            .ToList();

        if (removedIds.Count == 0)
        {
            return new CatalogPurgeResult(state, []);
        }

        var kept = state.Channels
            .Where(channel => channel.SourceOrigin != SourceOrigin.Catalog)
            .ToList();

        return new CatalogPurgeResult(state with { Channels = kept }, removedIds);
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
