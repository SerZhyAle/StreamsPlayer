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

    /// <summary>
    /// Whether a report - an engine error or an end of stream - raised under <paramref name="reportEpoch"/>
    /// still describes the attempt that is open now. The attempts of one leg share one engine, so the
    /// engine's identity cannot say; the epoch moves whenever an attempt is retired. A report read as
    /// current when it is not would be booked against the successor and skip that endpoint.
    /// </summary>
    public static bool IsCurrentAttempt(int reportEpoch, int currentEpoch) => reportEpoch == currentEpoch;
}

/// <summary>
/// SP-0203: which legs have an open call outstanding that a recovery started. It answers the one
/// question <see cref="BroadcastAttemptAdvance.CanAdvance"/> asks through <c>legOpenInFlight</c> - "is the
/// recovery that is in flight already waiting on its own leg's first open?" - and nothing else.
/// </summary>
/// <remarks>
/// Keyed by the leg, not by one flag and not by the attempt epoch. A shared flag lets an earlier call's
/// exit clear the mark of a later open that is still outstanding, and lets a stuck open of another leg
/// (a quality re-open) read as the recovery's own while the recovery is still deciding. An attempt epoch
/// would lose the mark the moment the leg advances to its next endpoint, although the recovery's open
/// call is still the leg's. Only a recovery's own open is ever registered. Not thread-safe: the player
/// drives it from the UI thread, where every leg start and every open's exit run.
/// </remarks>
public sealed class RecoveryLegOpenTracker
{
    private readonly Dictionary<int, int> _outstandingByLeg = new();

    /// <summary>A recovery's open call for <paramref name="leg"/> has started.</summary>
    public void Begin(int leg) =>
        _outstandingByLeg[leg] = _outstandingByLeg.GetValueOrDefault(leg) + 1;

    /// <summary>
    /// That open call has exited, by any path. Never touches another leg's mark and never goes below
    /// zero, so an unmatched exit cannot hide a later open.
    /// </summary>
    public void End(int leg)
    {
        if (!_outstandingByLeg.TryGetValue(leg, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _outstandingByLeg.Remove(leg);
            return;
        }

        _outstandingByLeg[leg] = count - 1;
    }

    /// <summary>Whether a recovery's open call for <paramref name="currentLeg"/> is still outstanding.</summary>
    public bool IsOutstanding(int currentLeg) => _outstandingByLeg.ContainsKey(currentLeg);
}
