namespace StreamsPlayer.Core;

/// <summary>
/// SP-0074: how one metadata attempt ended. The reader used to swallow every failure so that nothing
/// could disturb playback, which left "this station announces nothing" and "we could not read what it
/// announces" as the same observable event - silence. It still never throws at the caller; it returns
/// this instead, and the App writes one line of it to the session log.
/// </summary>
/// <remarks>
/// Values name what a person reading a log can act on, not what the exception type was. Two failures
/// that lead to the same conclusion share a value on purpose.
/// </remarks>
public enum IcyReadOutcome
{
    /// <summary>
    /// Playback stopped, switched, or failed and the read was torn down having never read a title.
    /// <para>The station connected and offered metadata; it just had not sent any by the time the user
    /// moved on. Distinct from <see cref="TitlesReported"/> on purpose - a read that was working is not
    /// the same event as one that was merely open, and a log that called both "cancelled" would answer
    /// none of the questions this ticket asks.</para>
    /// </summary>
    Cancelled,

    /// <summary>
    /// At least one track title was read and reported. The success this ticket is counting - and the
    /// value a live station normally ends on, because its read is torn down rather than finished.
    /// </summary>
    TitlesReported,

    /// <summary>The station answered, and said it carries no metadata. Nothing is wrong; this is the
    /// honest "this broadcaster does not tell anyone what is playing".</summary>
    NoMetadataOffered,

    /// <summary>The station offered metadata and then ended the stream before sending any.</summary>
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
