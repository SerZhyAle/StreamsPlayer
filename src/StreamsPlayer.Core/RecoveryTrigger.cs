namespace StreamsPlayer.Core;

/// <summary>
/// The classified cause of a live-playback interruption. It selects the retry budget and backoff
/// schedule fixed in the recovery contract (<c>docs/specifications/streams.txt</c>, Part D).
/// </summary>
public enum RecoveryTrigger
{
    /// <summary>Fell behind the live window; re-anchor to the live edge and re-prepare.</summary>
    BehindLiveWindow,

    /// <summary>Transient/retryable failure: connect/read timeout, connection failure, HTTP 429 or 5xx.</summary>
    Transient,

    /// <summary>A silent freeze detected by the stall watchdog (the backend threw no error).</summary>
    Stall,

    /// <summary>The backend reported the live stream ended; re-open it (backend adaptation, Part F).</summary>
    StreamEnded,

    /// <summary>Non-retryable: an explicit non-429 4xx, a malformed manifest, or an unsupported container.</summary>
    HardFail
}

/// <summary>Whether the policy wants another bounded reconnect, or a terminal hard failure.</summary>
public enum RecoveryActionKind
{
    Reconnect,
    HardFail
}

/// <summary>
/// The policy's decision for a single interruption: reconnect after <see cref="Delay"/> (this is
/// attempt <see cref="Attempt"/> of <see cref="Budget"/> for its trigger), or hard-fail. Deterministic
/// and clock-free — the caller waits <see cref="Delay"/> itself.
/// </summary>
public sealed record RecoveryDecision(
    RecoveryActionKind Kind,
    TimeSpan Delay,
    int Attempt,
    int Budget,
    RecoveryTrigger Trigger);
