namespace StreamsPlayer.Core;

/// <summary>
/// How an Icecast status metadata attempt ended, and whether it had already reported titles before
/// then. SP-0172: the outcome alone lost that fact, so an endpoint that fed titles for hours and then
/// died was logged like one that never worked.
/// </summary>
public readonly record struct IcecastStatusReadResult(IcecastStatusReadOutcome Outcome, bool TitlesReported);
