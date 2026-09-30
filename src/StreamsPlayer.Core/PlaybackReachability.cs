namespace StreamsPlayer.Core;

/// <summary>
/// SP-0041: what a failed open found when it asked, before any recovery attempt was spent, whether the
/// channel's host answers at all and whether there is a network to reach it over.
/// </summary>
public enum PlaybackReachability
{
    /// <summary>
    /// Not asked: a stall, a stream end or a behind-live event on a stream that was playing, or an address
    /// with no probeable endpoint. First, so a default value is exactly the behaviour before SP-0041.
    /// </summary>
    NotProbed,

    /// <summary>The stream's own host accepted a connection on the stream's own port.</summary>
    HostReachable,

    /// <summary>The host did not accept a connection while the network demonstrably works.</summary>
    ChannelUnreachable,

    /// <summary>
    /// There is no usable network, or a host on the user's own network did not answer - either way the
    /// channel was never reached, which is not the same as proven broken.
    /// </summary>
    NetworkUnreachable
}

/// <summary>SP-0041: the two consequences of a <see cref="PlaybackReachability"/>, stated once.</summary>
public static class PlaybackReachabilityRules
{
    /// <summary>
    /// Whether the bounded recovery ladder is worth running. A host that refuses the very connection the
    /// engine needs cannot be re-opened into playing (Decision 3), and with no network nothing can be
    /// (Decision 4) - both go straight to the verdict instead of spending seconds to learn it again.
    /// </summary>
    public static bool SpendsRecoveryBudget(PlaybackReachability reachability) =>
        reachability is PlaybackReachability.NotProbed or PlaybackReachability.HostReachable;

    /// <summary>
    /// Whether the verdict may offer to hide or delete the channel. Withheld only when the channel was never
    /// reached (Decisions 4 and 5): acting on it would destroy a possibly healthy entry over a network
    /// fault, and for a user's own row that delete cannot be undone.
    /// </summary>
    public static bool AllowsChannelRemoval(PlaybackReachability reachability) =>
        reachability != PlaybackReachability.NetworkUnreachable;
}
