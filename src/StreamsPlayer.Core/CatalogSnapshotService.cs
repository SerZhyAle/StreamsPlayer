namespace StreamsPlayer.Core;

/// <summary>
/// SP-0052: applies the bundled snapshot through the same merge and persistence path an explicit online
/// refresh uses, with the two differences decisions 4 and 5 call for - it never removes, and it never
/// stamps the moment of the last catalog download.
/// <para>
/// Deliberately has no <see cref="HttpClient"/> and no address: the non-goal "no second download
/// address" is enforced by the type's shape rather than by review. It is called only from a user action
/// the user took in that moment; there is no automatic application.
/// </para>
/// </summary>
public sealed class CatalogSnapshotService
{
    private readonly StreamCatalogStore _store;

    public CatalogSnapshotService(StreamCatalogStore store)
    {
        _store = store;
    }

    /// <summary>Reads the build's embedded snapshot and applies it.</summary>
    public Task<CatalogSnapshotApplyResult> ApplyBundledAsync(
        CatalogState currentState,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(BundledCatalogSnapshot.Read(), currentState, cancellationToken);

    public async Task<CatalogSnapshotApplyResult> ApplyAsync(
        CatalogSnapshot snapshot,
        CatalogState currentState,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The bundled catalog snapshot contains no valid channels.");
        }

        var merge = CatalogMerger.Merge(
            currentState.Channels,
            snapshot.Bank.Entries,
            DateTimeOffset.UtcNow,
            new CatalogMergeOptions(RemoveMissing: false, FaviconSource: FaviconSource.Snapshot));

        // LastCatalogRefreshAt is deliberately left alone: bundled data must not claim to be a download,
        // and the interface keeps inviting the user to update precisely because that field is still unset.
        var state = currentState with
        {
            Channels = merge.Channels.ToList(),
            AppliedSnapshotDate = snapshot.SourceDate
        };

        // The same conditional the online refresh uses, for the same reason: a bank whose atlas is absent
        // - or one this reader rejected for size - must not delete the atlas already installed in its slot.
        state = await _store.SaveAsync(
            state,
            snapshot.Bank.FaviconAtlas,
            replaceAtlas: snapshot.Bank.FaviconAtlas is { Length: > 0 },
            AtlasSlot.Snapshot,
            cancellationToken);

        return new CatalogSnapshotApplyResult(state, merge.Added, merge.Updated, snapshot.SourceDate);
    }
}
