namespace StreamsPlayer.Core;

/// <summary>A downloaded stream-bank build, ready to be applied to whatever catalog state is current.</summary>
public sealed record CatalogRefreshOutcome(StreamBank Bank, DateTimeOffset RefreshedAt)
{
    /// <summary>Whether the outcome carries the only atlas that may replace the catalog atlas.</summary>
    public bool ReplacesAtlas => Bank.FaviconAtlas is { Length: > 0 };

    /// <summary>
    /// Merges this build into <paramref name="currentState"/> at commit time, rather than into the state
    /// that existed when the download started.
    /// </summary>
    public CatalogRefreshResult Apply(CatalogState currentState)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        if (Bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The downloaded catalog contains no valid channels.");
        }

        // An atlas index is meaningful only against the atlas in the same build. A degraded build keeps
        // the old file but deliberately clears its new indices, avoiding confidently wrong channel icons.
        var entries = ReplacesAtlas
            ? Bank.Entries
            : [.. Bank.Entries.Select(entry => entry with { FaviconIndex = null })];

        var merge = CatalogMerger.Merge(
            currentState.Channels,
            entries,
            RefreshedAt,
            CatalogMergeOptions.CatalogRefresh with { ReplacesAtlas = ReplacesAtlas },
            channelsWithUserData: UserAuthoredChannels.Identify(currentState));
        var channels = merge.Channels.ToList();
        var state = FaviconAtlasReferences.ReleaseUnreferenced(currentState with
        {
            Channels = channels,
            LastCatalogRefreshAt = RefreshedAt
        }, AtlasSlot.Catalog);

        // A catalog refresh reclaims all snapshot rows. Once none remain, the snapshot atlas and date
        // describe no data and must be released by the following store save.
        if (!channels.Any(channel => channel.FaviconSource == FaviconSource.Snapshot))
        {
            state = state with { SnapshotAtlasFileName = null, AppliedSnapshotDate = null };
        }

        return new CatalogRefreshResult(
            state,
            merge.Added,
            merge.Updated,
            merge.Removed,
            ReplacesAtlas,
            merge.Retired);
    }
}
