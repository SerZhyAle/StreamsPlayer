namespace StreamsPlayer.Core;

/// <summary>
/// SP-0126: who owns a channel row, and what an edit does to that. A row that came from a bank - the
/// published one (<see cref="SourceOrigin.Catalog"/>) or a bank file the user imported
/// (<see cref="SourceOrigin.LocalCatalog"/>) - is the bank's until the user edits it; from then on it is the
/// user's, and no later refresh or import may write over it or bring its original address back beside it.
/// </summary>
public static class ChannelOwnership
{
    /// <summary>True for a row a bank merge may still update: published or imported bank rows.</summary>
    public static bool IsBankSourced(SourceOrigin origin) =>
        origin is SourceOrigin.Catalog or SourceOrigin.LocalCatalog;

    /// <summary>
    /// Whether <paramref name="channel"/> is kept out of the browse list and the random-station draw by the
    /// hidden set. Only bank rows can be hidden: a user's own row is deleted instead.
    /// </summary>
    /// <param name="hiddenIdentities">
    /// <see cref="CatalogState.HiddenCatalogUrls"/> already passed through <see cref="CatalogUrlIdentity.Normalize"/>.
    /// </param>
    public static bool IsHidden(IReadOnlySet<string> hiddenIdentities, StreamChannel channel) =>
        hiddenIdentities.Count > 0 &&
        IsBankSourced(channel.SourceOrigin) &&
        hiddenIdentities.Contains(CatalogUrlIdentity.Normalize(channel.Url));

    /// <summary>
    /// Applies a user edit to one row. A bank row becomes <see cref="SourceOrigin.Manual"/> first, so the
    /// merge no longer touches it, and drops its favicon index, which points into a sheet the next merge
    /// replaces (SP-0125). When the edit moves such a row to another address, the original address is hidden,
    /// so the bank listing it again adds a hidden row rather than a visible duplicate.
    /// </summary>
    /// <returns><paramref name="state"/> unchanged when no row has <paramref name="channelId"/>.</returns>
    public static CatalogState ApplyEdit(CatalogState state, Guid channelId, Func<StreamChannel, StreamChannel> edit)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(edit);

        var current = state.Channels.FirstOrDefault(channel => channel.Id == channelId);
        if (current is null)
        {
            return state;
        }

        var bankSourced = IsBankSourced(current.SourceOrigin);
        var owned = bankSourced
            ? current with { SourceOrigin = SourceOrigin.Manual, FaviconIndex = null }
            : current;
        var replacement = edit(owned) with { Id = current.Id };
        var hidden = bankSourced &&
                     !CatalogUrlIdentity.SameIdentity(current.Url, replacement.Url) &&
                     !CatalogUrlIdentity.IsHidden(state.HiddenCatalogUrls, current.Url)
            ? [.. state.HiddenCatalogUrls, current.Url]
            : state.HiddenCatalogUrls;
        return state with
        {
            Channels = [.. state.Channels.Select(channel => channel.Id == channelId ? replacement : channel)],
            HiddenCatalogUrls = hidden
        };
    }
}
