namespace StreamsPlayer.Core;

/// <summary>
/// Backend-neutral inputs describing one playback interruption. The App gathers these - media-backend
/// reason tokens (LibVLC, FlyleafLib), an optional failure-path HTTP status probe, and stall/live-window
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
    bool OpenTimedOut = false,
    // SP-0189: the player established that its own engine failed - it refused to start the connection, or the
    // engine, its player or its media could not be created - so no re-open can succeed. A flag for the same
    // reason as OpenTimedOut: the radio used to say this with an exception type name it stamped on every engine
    // error, network ones included, and the classifier could not tell the two apart.
    bool LocalEngineFailure = false,
    // SP-0284 (STREAM-BANK 2.10, item AB): the engine itself refused the stream's security certificate - the
    // measured trail of both LibVLC paths names it (EngineFailureTrail.UntrustedCertificate) - so no re-open can
    // succeed and the listener is told at once. A flag for the same reason as the two above: the reason token of
    // an engine error is the fixed "encountered_error", which the substring rules read as an ordinary transient.
    bool UntrustedCertificate = false)
{
    /// <summary>
    /// SP-0284: the reason token a certificate refusal travels under, from the failure signal to the verdict
    /// dialog and to <see cref="PlaybackErrorClassifier"/>. A token and a flag, because the flag decides the
    /// recovery and the token names the failure everywhere that only reads a reason (the log, the report, a
    /// waiting command's answer).
    /// </summary>
    public const string UntrustedCertificateReason = "untrusted_certificate";
}
