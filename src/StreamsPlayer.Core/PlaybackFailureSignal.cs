namespace StreamsPlayer.Core;

/// <summary>
/// Backend-neutral inputs describing one playback interruption. The App gathers these - media-backend
/// reason tokens (VLC/MediaElement), an optional failure-path HTTP status probe, and stall/live-window
/// watchdog flags - and <see cref="PlaybackRecoveryClassifier"/> maps them to a <see cref="RecoveryTrigger"/>.
/// </summary>
public sealed record PlaybackFailureSignal(
    string? Reason,
    int? HttpStatusCode = null,
    bool EndReached = false,
    bool Stall = false,
    bool BehindLiveWindow = false,
    // SP-0096: the leg was opened and never reached the screen inside its budget. Set by the player
    // itself, not read off the engine - which is exactly why it is a flag and not a reason token: a
    // reason string saying "open_timeout" would be classified by the substring rules as an ordinary
    // transient timeout and would spend the wrong budget.
    bool OpenTimedOut = false);
