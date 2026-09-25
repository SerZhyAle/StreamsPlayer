namespace StreamsPlayer.Core;

/// <summary>Why the catalog list does not offer a channel whatever the search and facets say.</summary>
public enum CatalogExclusion
{
    /// <summary>Nothing structural keeps the channel out; only the user's search or facets can.</summary>
    None,

    /// <summary>The channel is in the adult rubric and "Hide adult channels" is on (SP-0063).</summary>
    AdultHidden,

    /// <summary>The bank stopped publishing the channel and it is kept, not offered (SP-0089).</summary>
    Retired
}

/// <summary>
/// SP-0132: the list rules that no search text or facet can undo, in one place.
/// </summary>
/// <remarks>
/// The catalog view applies them on every rebuild, and "reveal this channel" has to know them before it
/// clears the user's filters: clearing everything for a channel these rules keep out anyway throws the
/// user's search away and still shows nothing. One home is what keeps the two from drifting apart.
/// Hiding by address is deliberately not here - it has its own identity set and its own restore path.
/// </remarks>
public static class CatalogOffer
{
    /// <param name="channel">The channel in question.</param>
    /// <param name="hideAdultContent">The "Hide adult channels" setting.</param>
    /// <param name="browsingCollection">
    /// Whether the view is scoped to one collection. A retired row stays where the user put it, so inside a
    /// collection it is still shown; in the general list only a pinned one is.
    /// </param>
    public static CatalogExclusion Exclusion(StreamChannel channel, bool hideAdultContent, bool browsingCollection)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (hideAdultContent && CatalogTopics.IsAdult(channel.Topic))
        {
            return CatalogExclusion.AdultHidden;
        }

        if (channel.RetiredAt is not null && !channel.Pinned && !browsingCollection)
        {
            return CatalogExclusion.Retired;
        }

        return CatalogExclusion.None;
    }
}
