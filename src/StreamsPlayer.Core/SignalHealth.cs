namespace StreamsPlayer.Core;

/// <summary>
/// SP-0045: what the player's stripe says about the stream being watched right now.
/// Session-scoped and never persisted - it describes this playback, not the channel.
/// </summary>
public enum SignalHealth
{
    /// <summary>
    /// Nothing is claimed yet: the stream has not reached live for the first time and has not
    /// failed. An ordinary two-second open must not flash red, so the opening phase carries no
    /// health meaning at all.
    /// </summary>
    Unknown,

    /// <summary>Playing, and nothing has disturbed it for a whole clean interval.</summary>
    Good,

    /// <summary>Playing, but it stalled, rebuffered, froze, or is losing pictures.</summary>
    Degraded,

    /// <summary>No signal: playback dropped and the player is reconnecting, or the channel failed.</summary>
    Lost
}

/// <summary>
/// SP-0045: the decoder/demux loss counters the health rule reads, in engine-neutral form.
/// Monotonic totals for the media currently open, not deltas - the rule differences them itself,
/// which is what lets it ignore the reset that comes with a new media.
/// </summary>
/// <param name="LostPictures">Pictures the decoder dropped.</param>
/// <param name="Corrupted">Corrupted input blocks the demuxer saw.</param>
/// <param name="Discontinuities">Discontinuities the demuxer saw in the input.</param>
public readonly record struct DecoderLossCounters(long LostPictures, long Corrupted, long Discontinuities);
