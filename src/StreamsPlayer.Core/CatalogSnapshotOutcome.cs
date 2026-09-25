namespace StreamsPlayer.Core;

/// <summary>A validated bundled snapshot ready to merge into the state current at commit time.</summary>
public sealed record CatalogSnapshotOutcome(CatalogSnapshot Snapshot)
{
    public bool ReplacesAtlas => Snapshot.Bank.FaviconAtlas is { Length: > 0 };

    public CatalogSnapshotApplyResult Apply(CatalogState currentState)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        if (Snapshot.Bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The bundled catalog snapshot contains no valid channels.");
        }

        var entries = ReplacesAtlas
            ? Snapshot.Bank.Entries
            : [.. Snapshot.Bank.Entries.Select(entry => entry with { FaviconIndex = null })];
        var merge = CatalogMerger.Merge(
            currentState.Channels,
            entries,
            DateTimeOffset.UtcNow,
            new CatalogMergeOptions(
                RemoveMissing: false,
                FaviconSource: FaviconSource.Snapshot,
                ReplacesAtlas: ReplacesAtlas,
                RevivesRetired: false));
        return new CatalogSnapshotApplyResult(
            FaviconAtlasReferences.ReleaseUnreferenced(currentState with
            {
                Channels = merge.Channels.ToList(),
                AppliedSnapshotDate = Snapshot.SourceDate
            }, AtlasSlot.Snapshot),
            merge.Added,
            merge.Updated,
            Snapshot.SourceDate);
    }
}
