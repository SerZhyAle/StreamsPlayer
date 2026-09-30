namespace StreamsPlayer.Core;

/// <summary>
/// SP-0074: why one metadata attempt ended. The reader used to swallow every failure so that nothing
/// could disturb playback, which left "this station announces nothing" and "we could not read what it
/// announces" as the same observable event - silence. It still never throws at the caller; it returns
/// this instead, and the App writes one line of it to the session log.
/// </summary>
/// <remarks>
/// Values name what a person reading a log can act on, not what the exception type was. Two failures
/// that lead to the same conclusion share a value on purpose.
/// <para>SP-0172: whether titles were reported before the attempt ended is not a value here but the
/// <see cref="IcyReadResult.TitlesReported"/> flag on the result - one flag instead of a reported-titles
/// twin of every value, so a new way to fail cannot forget the fact.</para>
/// </remarks>
public enum IcyReadOutcome
{
    /// <summary>
    /// Playback stopped, switched, or failed and the read was torn down. Whether the station had
    /// already been announcing tracks is the result's <see cref="IcyReadResult.TitlesReported"/>:
    /// a read that was working is not the same event as one that was merely open.
    /// </summary>
    Cancelled,

    /// <summary>The station answered, and said it carries no metadata. Nothing is wrong; this is the
    /// honest "this broadcaster does not tell anyone what is playing".</summary>
    NoMetadataOffered,

    /// <summary>The station offered metadata and then ended the stream.</summary>
    StreamEnded,

    /// <summary>
    /// The station's greeting was refused by the standard HTTP stack - a Shoutcast v1 daemon answering
    /// <c>ICY 200 OK</c> instead of <c>HTTP/1.1 200 OK</c>.
    /// <para>Kept as a value even though the plaintext fallback now handles that greeting, because a
    /// TLS station cannot use that fallback and still reports it. It is the counter that says whether
    /// this class is still costing anything.</para>
    /// </summary>
    StatusLineRefused,

    /// <summary>The station could not be reached: DNS, connect, or TLS.</summary>
    Unreachable,

    /// <summary>The station accepted the connection and then said nothing within the deadline.</summary>
    TimedOut,

    /// <summary>The station answered in a shape this reader could not use.</summary>
    Malformed
}
