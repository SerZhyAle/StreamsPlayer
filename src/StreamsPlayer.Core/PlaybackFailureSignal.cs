namespace StreamsPlayer.Core;

/// <summary>
/// Backend-neutral inputs describing one playback interruption. The App gathers these — media-backend
/// reason tokens (VLC/MediaElement), an optional failure-path HTTP status probe, and stall/live-window
/// watchdog flags — and <see cref="PlaybackRecoveryClassifier"/> maps them to a <see cref="RecoveryTrigger"/>.
/// </summary>
public sealed record PlaybackFailureSignal(
    string? Reason,
    int? HttpStatusCode = null,
    bool EndReached = false,
    bool Stall = false,
    bool BehindLiveWindow = false);
