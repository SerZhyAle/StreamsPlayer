namespace StreamsPlayer.Core;

/// <summary>
/// SP-0089, source contract item D: which channels carry something the user made, and may therefore not
/// be deleted merely because a bank build stopped listing their URL.
/// </summary>
/// <remarks>
/// <para>This is the one place the answer is written down, and the reason it is a class rather than a
/// condition inside <see cref="CatalogMerger"/> is that the set is expected to grow. Every future feature
/// that attaches a user-made value to a channel has to be added here, or a refresh will silently delete
/// it the first time the producer's liveness probe has a bad day. The cost of forgetting is not a broken
/// build, it is a user losing something they made - so the list lives under one name that a new feature's
/// author can be pointed at.</para>
/// <para>Deliberately excluded, each because the value already survives the row on its own terms:</para>
/// <list type="bullet">
/// <item><description><see cref="CatalogState.HiddenCatalogUrls"/> - authored by the user, but keyed by
/// normalized URL in its own list, so it outlives the row and re-applies by itself if the URL returns.
/// Keeping the row would also contradict the request it records: the user asked not to see this
/// channel.</description></item>
/// <item><description>Quality memory - keyed by URL in its own file, and a cache whose loss costs one
/// relearned probe.</description></item>
/// <item><description><see cref="StreamChannel.LastPlayedAt"/>, <see cref="StreamChannel.LastPlayOutcome"/>
/// and <see cref="StreamChannel.SortIndex"/> - observed or derived rather than authored. A channel the
/// user actually cared about carries a pin or a history entry as well; one that carries only an outcome
/// stamp is a row the application wrote about itself.</description></item>
/// </list>
/// <para>Listening history counts, and it is the one entry worth defending: it accumulates without an
/// explicit act, which is exactly why contract item D names it. It is also the only record of what the
/// user has been listening to, and rebuilding it is not possible - a pin can be re-pinned from memory,
/// a month of history cannot be re-listened.</para>
/// </remarks>
public static class UserAuthoredChannels
{
    /// <summary>Ids of every channel in <paramref name="state"/> that carries user-authored data.</summary>
    /// <remarks>
    /// Pins are read from the rows rather than taken as given by the caller so that one call answers the
    /// whole question; <see cref="CatalogMerger"/> can see <see cref="StreamChannel.Pinned"/> itself, but
    /// splitting the rule across two places is how half of it gets forgotten.
    /// </remarks>
    public static HashSet<Guid> Identify(CatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var authored = new HashSet<Guid>();
        foreach (var channel in state.Channels)
        {
            if (channel.Pinned)
            {
                authored.Add(channel.Id);
            }
        }

        foreach (var collection in state.Collections)
        {
            foreach (var channelId in collection.ChannelIds)
            {
                authored.Add(channelId);
            }
        }

        foreach (var entry in state.ListeningHistory)
        {
            authored.Add(entry.ChannelId);
        }

        return authored;
    }
}
