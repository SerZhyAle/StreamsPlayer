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


    /// <summary>Validates a snapshot and returns an outcome that can be applied to the latest state.</summary>
    public static CatalogSnapshotOutcome Prepare(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The bundled catalog snapshot contains no valid channels.");
        }

        return new CatalogSnapshotOutcome(snapshot);
    }

    public async Task<CatalogSnapshotApplyResult> ApplyAsync(
        CatalogSnapshot snapshot,
        CatalogState currentState,
        CancellationToken cancellationToken = default)
    {
        var outcome = Prepare(snapshot);
        var result = outcome.Apply(currentState);
        var state = await _store.SaveAsync(
            result.State,
            outcome.Snapshot.Bank.FaviconAtlas,
            outcome.ReplacesAtlas,
            AtlasSlot.Snapshot,
            cancellationToken);
        return result with { State = state };
    }
}
