using System.Net.Sockets;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0041 / SP-0168: what a failed open found when it asked, before any recovery attempt was spent, whether
/// the channel's own host answers. Only a definite answer skips the ladder; everything else keeps it.
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

    /// <summary>The stream's own host actively refused the connection - a definite answer.</summary>
    ChannelUnreachable,

    /// <summary>
    /// There is no usable network, or a host on the user's own network refused or is not known - either way
    /// the channel was never proven broken, which is not the same as reached.
    /// </summary>
    NetworkUnreachable,

    /// <summary>
    /// SP-0168: the host name does not resolve (the resolver said it does not exist) - a definite answer with
    /// its own reason, distinct from a refusal.
    /// </summary>
    NameNotResolved,

    /// <summary>
    /// SP-0168: the probe learned nothing definite - a timeout, a transient resolver failure, a routing error.
    /// A slow answer is not a refusal, so the ladder runs exactly as if nothing had been asked.
    /// </summary>
    Inconclusive
}

/// <summary>SP-0168: how one TCP connect to the channel's own host ended, before it becomes a verdict.</summary>
public enum ConnectOutcome
{
    /// <summary>The connection was accepted.</summary>
    Connected,

    /// <summary>The host answered with a refusal (TCP reset).</summary>
    Refused,

    /// <summary>The name does not exist according to the resolver.</summary>
    NameNotResolved,

    /// <summary>Anything else: the time budget ran out, a transient failure, a routing error.</summary>
    Inconclusive
}

/// <summary>SP-0041 / SP-0168: the verdict mapping and its two consequences, stated once.</summary>
public static class PlaybackReachabilityRules
{
    /// <summary>
    /// The outcome of a socket failure. Only <see cref="SocketError.ConnectionRefused"/> is a refusal and only
    /// <see cref="SocketError.HostNotFound"/> / <see cref="SocketError.NoData"/> is a missing name; a transient
    /// resolver failure (<see cref="SocketError.TryAgain"/>), a timeout and every routing error prove nothing.
    /// </summary>
    public static ConnectOutcome OutcomeOf(SocketError error) => error switch
    {
        SocketError.Success => ConnectOutcome.Connected,
        SocketError.ConnectionRefused => ConnectOutcome.Refused,
        SocketError.HostNotFound or SocketError.NoData => ConnectOutcome.NameNotResolved,
        _ => ConnectOutcome.Inconclusive
    };

    /// <summary>
    /// The verdict for one connect to the channel's own host. A timeout never skips the ladder (SP-0168); a
    /// refusal or a missing name on a host of the user's own network is "never reached" rather than "broken"
    /// (SP-0041 Decision 5), because a camera that is switched off is not a dead channel.
    /// </summary>
    public static PlaybackReachability Verdict(ConnectOutcome outcome, bool isLocalHost) => outcome switch
    {
        ConnectOutcome.Connected => PlaybackReachability.HostReachable,
        ConnectOutcome.Refused => isLocalHost
            ? PlaybackReachability.NetworkUnreachable
            : PlaybackReachability.ChannelUnreachable,
        ConnectOutcome.NameNotResolved => isLocalHost
            ? PlaybackReachability.NetworkUnreachable
            : PlaybackReachability.NameNotResolved,
        _ => PlaybackReachability.Inconclusive
    };

    /// <summary>
    /// Whether the bounded recovery ladder is worth running. A host that refuses the very connection the
    /// engine needs, or a name that does not exist, cannot be re-opened into playing (Decision 3), and with no
    /// network nothing can be (Decision 4) - those go straight to the verdict. A timeout or any unknown keeps
    /// the ladder.
    /// </summary>
    public static bool SpendsRecoveryBudget(PlaybackReachability reachability) =>
        reachability is PlaybackReachability.NotProbed
            or PlaybackReachability.HostReachable
            or PlaybackReachability.Inconclusive;

    /// <summary>
    /// Whether the verdict may offer to hide or delete the channel. Withheld only when the channel was never
    /// reached (Decisions 4 and 5): acting on it would destroy a possibly healthy entry over a network
    /// fault. The delete of a user's own row is confirmed by the dialog itself (`APP-BEHAVIOUR` rule 9).
    /// </summary>
    public static bool AllowsChannelRemoval(PlaybackReachability reachability) =>
        reachability != PlaybackReachability.NetworkUnreachable;
}
