namespace StreamsPlayer.Core;

/// <summary>
/// SP-0203: when a failed attempt of a leg may hand the open to the producer's next listed endpoint.
/// </summary>
/// <remarks>
/// Moving to the next endpoint is a pre-live move and nothing else. A leg that has shown its picture and
/// then errors is a reconnect, and a reconnect restarts the list from the top through the recovery policy:
/// advancing instead would silently trade a playing LAN endpoint for the relay, and the new endpoint's
/// opening buffer fill would then be booked as a live stall and a quality-starvation strike.
/// <para>
/// A recovery that is deciding (probing, waiting out its backoff) owns the next open and no attempt may
/// race it; once it has started its leg and is waiting for the engine's open call, that leg is a fresh
/// pre-live leg again, and its own first attempt failing must still reach the next endpoint.
/// </para>
/// </remarks>
public static class BroadcastAttemptAdvance
{
    public static bool CanAdvance(
        bool closing,
        bool failureShown,
        bool probeLegPending,
        bool reachedLive,
        bool recoveryInFlight,
        bool legOpenInFlight,
        int attemptIndex,
        int attemptCount) =>
        !closing
        && !failureShown
        && !probeLegPending
        && !reachedLive
        && (!recoveryInFlight || legOpenInFlight)
        && attemptIndex + 1 < attemptCount;
}
