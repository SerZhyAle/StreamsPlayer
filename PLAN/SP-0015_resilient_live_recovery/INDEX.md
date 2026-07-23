# SP-0015 tactical plan — Resilient live-stream recovery

**Status:** Phases 1–5 implemented; build + 89 tests pass; recovery state machine proven live on both
backends. Remaining: visual GUI acceptance (BlockNeedUserTest) — see the ticket's Verification section.

Strategic ticket: [../SP-0015_resilient_live_recovery.md](../SP-0015_resilient_live_recovery.md)
Recovery contract: `docs/specifications/streams.txt` Part D. Tuning baseline: `docs/stream-playback-recommendations.md`.

## Design summary

The recovery *policy* (budgets, backoff sequences, error classification, reset-on-live) is a pure,
platform-neutral state machine in `StreamsPlayer.Core` so it is unit-testable (AC 6). The media
backends in `StreamsPlayer.App` gather raw signals, feed the policy, and apply its decisions
(reconnect after a cancellable backoff, or hand off to the terminal failure dialog).

Part D budgets/backoff encoded by the policy:

| Trigger | Detection | Budget | Backoff |
| --- | --- | --- | --- |
| `BehindLiveWindow` | backend "behind live window" signal | 3 | linear 1 s / 2 s / 3 s |
| `Transient` | connect/read timeout, connection failure, HTTP 429 or 5xx | 4 | exponential 2 / 4 / 8 / 16 s |
| `Stall` | nominally playing but position advances < 500 ms across 3× 3 s polls, or buffering > 15 s with no byte progress | 3 | short fixed (1 s) |
| `StreamEnded` | backend EndReached on a live stream (backend adaptation, Part F) | 4 | short fixed (1 s) |
| `HardFail` | explicit non-429 4xx, malformed manifest, unsupported container | 0 | — (immediate terminal) |

Budgets are **consecutive**: reaching sustained live playback resets every counter, so a looping
playlist (reaches live each loop) never exhausts a budget while a genuinely dead stream terminates
quickly. This preserves the existing looping-playlist tolerance without the old unbounded 30-retry loop.

Reconciliation of Part D stall-watchdog with tuning §4 ("never reconnect to grow the buffer;
rebuffer in place"): genuine rebuffering (bytes flowing, < 15 s) is left alone; only a *silent
freeze* (nominally playing, position frozen) or a stuck buffer (> 15 s, no byte progress) triggers a
stop/re-prepare. The two documents are complementary, not contradictory.

Classifying 429/5xx vs non-429 4xx (AC 1/AC 2) requires an HTTP status the media backends hide, so
the App runs a bounded, failure-path-only status probe (http/https only; short timeout; never on the
grid-preview path). RTSP failures have no HTTP status and default to `Transient`.

Audio scope: the WPF `MediaElement` audio path (unchanged backend per SP-0026) gets bounded
`Transient` reconnect-with-backoff and a Reconnecting label. The position-based stall watchdog is
video/RTSP only (MediaElement exposes no reliable live position/byte telemetry); documented limit.

## Phases (dependency-ordered)

| Phase | Produces | Consumes |
| --- | --- | --- |
| [PHASE-1](PHASE-1_core_recovery_policy.md) — Core policy, classifier, tests | `LivePlaybackRecoveryPolicy`, `PlaybackRecoveryClassifier`, `RecoveryTrigger`, `RecoveryDecision`, `PlaybackFailureSignal` + Core tests | Part D constants |
| [PHASE-2](PHASE-2_video_rtsp_recovery.md) — Video/RTSP wiring | recovery coordinator, stall watchdog, status probe, cancellation, Reconnecting label in `PlayerWindow` | Phase 1 |
| [PHASE-3](PHASE-3_audio_recovery.md) — Audio wiring | bounded audio reconnect + Reconnecting now-playing label + cancellation in MainWindow | Phase 1 |
| [PHASE-4](PHASE-4_localization_and_isolation.md) — Strings, docs, grid isolation | EN/RU Reconnecting strings; README/tuning note; grid-preview isolation check | Phase 2, Phase 3 |
| [PHASE-5](PHASE-5_validation.md) — Validation | build/test evidence + run-and-observe PASS/FAIL | Phases 1–4 |

## Criterion / constraint coverage

| Spec item | Phase(s) |
| --- | --- |
| AC 1 retryable (timeout/conn/429/5xx/behind-live/stall) bounded policy | 1, 2, 3 |
| AC 2 non-429 4xx / malformed / unsupported fail without spending transient budget | 1, 2 |
| AC 3 Buffering vs Reconnecting distinct in EN+RU incl. attempt outcome | 2, 3, 4 |
| AC 4 stop / close / switch cancels waits, no old-stream restart | 2, 3 |
| AC 5 exhausted → Retry + Close; Remove only for removable user rows | 2, 3 (reuse `PlaybackFailureDialog`) |
| AC 6 automated policy/state tests + run-and-observe | 1, 5 |
| Constraint: Part D budgets/backoff/live-edge/buffering/stall | 1, 2 |
| Constraint: UI distinguishes states, cancellable | 2, 3, 4 |
| Constraint: new choice cancels previous recovery | 2, 3 |
| Constraint: terminal outcome + retry action; provenance-gated removal | 2, 3 |
| Constraint: grid-preview failures never recover or write fail mark | 4 |
