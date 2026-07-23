# PHASE-1 — Core recovery policy, classifier, and tests

**Produces:** `LivePlaybackRecoveryPolicy`, `PlaybackRecoveryClassifier`, `RecoveryTrigger`,
`RecoveryDecision`, `PlaybackFailureSignal` in `StreamsPlayer.Core`, plus xUnit coverage.
**Consumes:** Part D constants only. No dependency on Phase 2+.

Core stays platform-neutral: no timers, no clock, no HTTP, no media types. The policy is a pure
state object; the App supplies signals and applies the returned delay/decision.

## Steps

1. **New file `src/StreamsPlayer.Core/RecoveryTrigger.cs`.**
   - `public enum RecoveryTrigger { BehindLiveWindow, Transient, Stall, StreamEnded, HardFail }`.
   - `public enum RecoveryActionKind { Reconnect, HardFail }`.
   - `public sealed record RecoveryDecision(RecoveryActionKind Kind, TimeSpan Delay, int Attempt, int Budget, RecoveryTrigger Trigger)`.
   - Static check: `dotnet build src/StreamsPlayer.Core -c Release`.

2. **New file `src/StreamsPlayer.Core/PlaybackFailureSignal.cs`.**
   - `public sealed record PlaybackFailureSignal(string? Reason, int? HttpStatusCode = null, bool EndReached = false, bool Stall = false, bool BehindLiveWindow = false)`.
   - Static check: builds with step 1.

3. **New file `src/StreamsPlayer.Core/PlaybackRecoveryClassifier.cs`** — pure, total map
   `PlaybackFailureSignal -> RecoveryTrigger`. Precedence (first match wins):
   - `Stall` if `signal.Stall`.
   - `BehindLiveWindow` if `signal.BehindLiveWindow` or reason contains "behind live window"/"live window".
   - `HttpStatusCode` present: 429 -> `Transient`; 500–599 -> `Transient`; other 400–499 -> `HardFail`.
   - reason contains unsupported/format/codec/container/"malformed"/"demux" and not a network token -> `HardFail`.
   - reason contains timeout/connection/network/socket/dns/http/"429"/"5xx"/refused/reset -> `Transient`.
   - `StreamEnded` if `signal.EndReached`.
   - default -> `Transient` (bank failures are predominantly transient availability; still budget-bounded).
   - Static check: builds.

4. **New file `src/StreamsPlayer.Core/LivePlaybackRecoveryPolicy.cs`** — `public sealed class`.
   - Private per-trigger attempt counters (`BehindLiveWindow`, `Transient`, `Stall`, `StreamEnded`).
   - `public RecoveryDecision Decide(PlaybackFailureSignal signal)`: classify via
     `PlaybackRecoveryClassifier`; if `HardFail`, return `HardFail` with `Attempt=0,Budget=0`;
     else increment that trigger's counter; if `attempt > budget` return `HardFail` (exhausted);
     else return `Reconnect` with the computed backoff for `attempt` (1-based).
   - Budgets/backoff (constants named after Part D):
     `BehindLiveWindow` budget 3, delay = `attempt * 1 s`;
     `Transient` budget 4, delay = `2^attempt s` (2,4,8,16);
     `Stall` budget 3, delay = 1 s;
     `StreamEnded` budget 4, delay = 1 s.
   - `public void NotifyLive()` / `public void Reset()`: zero all counters (called by App on sustained live).
   - Deterministic; no ambient state. Static check: builds.

5. **New test file `tests/StreamsPlayer.Core.Tests/LivePlaybackRecoveryPolicyTests.cs`** covering:
   - Transient budget/backoff sequence: attempts 1–4 -> Reconnect 2/4/8/16 s; attempt 5 -> HardFail.
   - BehindLiveWindow: 1–3 -> 1/2/3 s; 4 -> HardFail.
   - Stall: 3 reconnects at 1 s then HardFail. StreamEnded: 4 reconnects then HardFail.
   - `NotifyLive()`/`Reset()` restores full budget mid-sequence (looping-playlist case).
   - Independent counters: spending Transient budget does not reduce Stall budget.
   - Classifier: HTTP 429/503 -> Transient; 404/403 -> HardFail; "connection timeout" -> Transient;
     "unsupported codec" -> HardFail; behind-live-window reason -> BehindLiveWindow; stall/endReached flags;
     empty reason with no status -> Transient.
   - Static check: `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~LivePlaybackRecoveryPolicyTests"` -> all pass.

## Phase static check

`dotnet test StreamsPlayer.sln -c Release` — expected: build succeeds, all tests pass (existing + new).
