namespace StreamsPlayer.Core;

/// <summary>
/// SP-0125: an atlas slot is worth its file only while some row indexes into it. An imported bank's sheet
/// can be up to the atlas cap, and once a published refresh has absorbed every imported row nothing would
/// ever release it. Clearing the name is the whole job - the store's save sweeps every atlas file the saved
/// state no longer names.
/// </summary>
public static class FaviconAtlasReferences
{
    /// <param name="writtenSlot">
    /// The slot the calling operation's own save decides: it is replaced when the bank carried a sheet,
    /// and kept when it did not - SP-0088 keeps the installed sheet on a degraded bank, and releasing it
    /// here would turn "discard this build's indices" into "delete the atlas".
    /// </param>
    public static CatalogState ReleaseUnreferenced(CatalogState state, AtlasSlot writtenSlot)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool catalog = false, snapshot = false, imported = false;
        foreach (var channel in state.Channels)
        {
            if (channel.FaviconIndex is null)
            {
                continue;
            }

            switch (channel.FaviconSource)
            {
                case FaviconSource.Snapshot: snapshot = true; break;
                case FaviconSource.Imported: imported = true; break;
                default: catalog = true; break;
            }
        }

        return state with
        {
            AtlasFileName = catalog || writtenSlot == AtlasSlot.Catalog ? state.AtlasFileName : null,
            SnapshotAtlasFileName = snapshot || writtenSlot == AtlasSlot.Snapshot ? state.SnapshotAtlasFileName : null,
            ImportedAtlasFileName = imported || writtenSlot == AtlasSlot.Imported ? state.ImportedAtlasFileName : null
        };
    }
}
