namespace StreamsPlayer.Core;

/// <summary>
/// How an ICY metadata attempt ended, and whether it had already reported titles before then.
/// SP-0172: the outcome alone forgot that history, so an hour of titles followed by our own silence
/// timeout or a reset was logged as timed out or unreachable - indistinguishable from a read that
/// never produced anything.
/// </summary>
public readonly record struct IcyReadResult(IcyReadOutcome Outcome, bool TitlesReported);
