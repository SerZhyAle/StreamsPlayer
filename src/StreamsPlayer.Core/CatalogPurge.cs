namespace StreamsPlayer.Core;

/// <summary>
/// SP-0030: explicit, user-confirmed removal of every downloaded catalog row, so a user who only
/// wants their own RTSP or hand-added channels is not left browsing the shared stream bank.
/// The counterpart of the merge contract: <see cref="CatalogMerger"/> never touches user rows, and
/// neither does this - only <see cref="SourceOrigin.Catalog"/> rows are dropped. Everything else in
/// the state (hidden identities, favicon atlas, last refresh time, history, preferences) is left
/// as-is; the purge is not an opt-out, and an explicit refresh legitimately downloads them again.
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
}
