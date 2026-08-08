using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0052: the icon atlases currently installed, one per source. A channel's <c>FaviconIndex</c> is an
/// offset into the atlas that shipped with its own bank, so the path and the bound have to be resolved
/// per row rather than once for the whole list - otherwise a catalog holding both downloaded and
/// bundled rows renders one group's icons against the other's sheet, which shows the wrong picture
/// rather than none.
/// <para>
/// A record struct so <see cref="ChannelRow.UpdatePresentation"/> can keep its cheap "nothing changed"
/// early-out on structural equality.
/// </para>
/// </summary>
internal readonly record struct FaviconAtlasSet(
    string? CatalogPath,
    int? CatalogMaximumIndex,
    string? SnapshotPath,
    int? SnapshotMaximumIndex)
{
    public (string? Path, int? MaximumIndex) Resolve(FaviconSource source) => source switch
    {
        FaviconSource.Snapshot => (SnapshotPath, SnapshotMaximumIndex),
        _ => (CatalogPath, CatalogMaximumIndex)
    };
}
