# SP-0015: Resilient live-stream recovery

**Status:** Implemented — BlockNeedUserTest for visual GUI acceptance (exit: user runs the PHASE-5 run-and-observe steps and confirms the visible Reconnecting label, EN/RU distinction, cancellation gesture, and terminal dialog).

Tactical plan: [SP-0015_resilient_live_recovery/INDEX.md](SP-0015_resilient_live_recovery/INDEX.md)

## Goal

Recover audio, video, and RTSP playback from temporary network failures and silent stalls while clearly distinguishing ordinary buffering from reconnection.

## Why

Public live streams are routinely interrupted by relay, network, and live-window failures. A bounded recovery policy is more useful than immediately failing or spinning indefinitely without explaining the state.

## Non-goals

- Guarantee availability of a third-party stream.
- Refresh or replace catalog data automatically.
- Retry permanent client errors, malformed media, or unsupported formats indefinitely.
- Add background health monitoring for channels that the user is not playing.

## Constraints

- Recovery follows the retry budgets, backoff, live-edge, buffering, and stall-watchdog rules already fixed in `streams.txt`, Part D.
- The UI distinguishes localized Buffering and Reconnecting states and remains cancellable.
- A new user playback choice cancels recovery of the previous stream.
- Exhausted or non-retryable failures produce one terminal outcome and a clear retry action; removal is available only when the channel's provenance permits it.
- Grid-preview failures never trigger foreground recovery or write a failed-play mark.

## Acceptance criteria

1. Retryable connection, timeout, HTTP 429/5xx, behind-live-window, and silent-stall scenarios use the specified bounded recovery policy.
2. Explicit non-429 4xx responses, malformed manifests, and unsupported containers fail without consuming the transient retry budget.
3. Buffering and active reconnection are visibly distinct in English and Russian, including the current attempt outcome.
4. User stop, window close, or selection of another stream cancels outstanding waits and prevents the old stream from restarting.
5. Exhausted recovery exposes Retry and Close; Remove is offered only for removable user-owned rows.
6. Automated policy/state tests pass, and run-and-observe checks cover successful recovery, cancellation, and terminal failure for representative live media.

## Risks

Media backends report failures and progress differently across HTTP audio, HLS/DASH video, and RTSP. Recovery must avoid duplicate playback sessions and must not confuse a slow initial buffer with a frozen stream.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md) and the recovery contract in [streams specification](../docs/specifications/streams.txt).

## Verification (2026-07-21)

Recovery policy lives in `StreamsPlayer.Core` (`LivePlaybackRecoveryPolicy`, `PlaybackRecoveryClassifier`,
`RecoveryTrigger`, `PlaybackFailureSignal`); backends in App feed signals and apply decisions. App-side
signal gathering adds a failure-path-only HTTP status probe (`PlaybackStatusProbe`) because the media
backends hide the HTTP status needed to split retryable 429/5xx from permanent non-429 4xx.

Proven (automation + live session log, `expected | actual`):

- Build + tests — `./scripts/check.ps1`: **89/89 pass, 0 warnings** (21 new `LivePlaybackRecoveryPolicyTests`). AC 6 (policy tests).
- Audio transient recovery, live app (`--url http://127.0.0.1:1/dead.mp3`): expected 2/4/8/16 s backoff then hard-fail | actual `AUDIO RECOVER Reconnect attempt=1..4 delay_ms=2000/4000/8000/16000` (each reconnect fired at exactly the stated delay), then `action=HardFail attempt=5 budget=4 delay_ms=0` with no further reconnect. AC 1, AC 6 terminal.
- Video/RTSP transient recovery, live app (`--url http://127.0.0.1:1/x.m3u8`): expected classify + reconnect | actual `PLAYBACK RECOVER trigger=Transient attempt=1 budget=4 delay_ms=2000`, reconnect 2.0 s later with `cache_ms=4000`. AC 1.
- Happy path unaffected (`--url` real radio): `AUDIO OPEN → AUDIO LIVE`.
- Classification split (AC 2) — unit-tested: HTTP 403/404/451 → HardFail; 429/500/503 → Transient; unsupported/malformed reason → HardFail; a HardFail signal never consumes the transient budget.
- Grid-preview isolation — code check: no `RecordPlayOutcome`/policy/dialog reference in the preview path.

Remaining (BlockNeedUserTest) — GUI observation that cannot be screen-captured here:

1. Visible `Buffering… %` vs `Reconnecting… (attempt N of M)` distinction, in English and Russian (toggle language).
2. Cancellation by gesture — close the player / stop audio / switch stream during a backoff and confirm the old stream never restarts.
3. Terminal `PlaybackFailureDialog` (Retry / Copy report / Hide|Delete / Keep) after budget exhaustion.
4. A representative flaky live stream recovering back to live, and the stall watchdog on a real silent freeze.

Backend note: on the LibVLC backend a behind-live-window drop surfaces as EndReached; the `StreamEnded`
recovery path re-opens and re-anchors to the live edge, covering that scenario. The explicit
`BehindLiveWindow` policy path is implemented and unit-tested for backends that report it distinctly.
