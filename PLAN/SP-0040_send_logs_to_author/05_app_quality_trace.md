# Phase 05 - App: the playback-quality trace

**Status:** Planned

## Goal

Make the archived log answer "did the player cope with this bad stream, and how" for every
played channel, on both backends and for audio as well as video (spec Decision 6, criterion 12).

## What already exists (verified in the working tree, do not rebuild)

`PlayerWindow` records `PLAYBACK OPEN` (with the re-open reason and buffer target),
`PLAYBACK STALL` (buffer level, stall ordinal, elapsed), `PLAYBACK RESUME`, `PLAYBACK LIVE`
(time to first frame), `PLAYBACK RECOVER` (trigger, action, attempt, budget, delay, HTTP status),
`PLAYBACK WATCHDOG` (frozen / stuck buffer), `PLAYBACK FAIL` and `PLAYBACK CLOSE`, plus
`STATS` / `STALL STATS` / `RESUME STATS` from LibVLC every two seconds. `MainWindow` records
`AUDIO OPEN`, `AUDIO RECONNECT`, `AUDIO LIVE`, `AUDIO FAIL` and `AUDIO RECOVER`.

## Gaps this phase closes

1. **No session-level summary.** `PLAYBACK CLOSE` reports `watch_ms` from `_playbackClock`, which
   `StartMedia` **restarts on every reconnect**, so a session that reconnected four times reports
   only the last leg and no reconnect count. Fix: a second `Stopwatch` started once per window, a
   `_reconnectCount` incremented where a reconnect decision is applied, and a terminal-reason
   field, all emitted in one `PLAYBACK SESSION` record at close: `session_ms`, `legs`, `live`,
   `ttff_ms`, `stalls`, `reconnects`, `outcome` (`live` / `failed` / `never_live` / `closed`), `url`.
2. **Audio has no summary and no close record at all.** Add a session clock, a reconnect counter
   and a `live` flag around the audio session, emitted as `AUDIO SESSION` from
   `StopAudioPlayback` (the one funnel every stop, switch, pause and terminal failure passes
   through) with the same field shape as the video summary, minus the stall fields the platform
   cannot supply.
3. **The alternate backend is diagnostically blind.** `FlyleafVideoBackend.LogStats` is an empty
   method and its buffering/error events carry no record, so on that backend a stall shows up only
   as `PLAYBACK STALL` with no cause. Add `FLYLEAF BUFFER` records for buffering started and
   completed (with the success flag and the elapsed fill time) and a `FLYLEAF ERROR` record naming
   which event failed (open vs buffering vs playback stopped), and make `LogStats` emit
   `stats=unavailable` **once per session** rather than staying silent - the author must be able to
   tell "no statistics" from "no problem".

## Changes

- `src/StreamsPlayer.App/PlayerWindow.xaml.cs` - `_sessionClock`, `_reconnectCount`, `_legCount`,
  `_terminalReason`; increment where `RecoverAsync` applies a reconnect decision; record the
  summary in the existing close path next to `PLAYBACK CLOSE`.
- `src/StreamsPlayer.App/MainWindow.Diagnostics.cs` (new partial) - the audio session counters and
  the `AUDIO SESSION` emitter, called from `StopAudioPlayback`; also home to phase 06's action so
  `MainWindow.xaml.cs` gains calls only.
- `src/StreamsPlayer.App/MainWindow.xaml.cs` - start/advance the audio session counters in
  `StartAudioPlayback` (open vs reconnect is already a parameter) and call the emitter from
  `StopAudioPlayback`.
- `src/StreamsPlayer.App/FlyleafVideoBackend.cs` - the buffer/error records and the one-shot
  `stats=unavailable`.

## Constraints held

No new timer, no per-frame or per-tick record, no change to any recovery decision, buffer target
or user-visible label - the records are emitted beside existing decisions, never in place of them.

## Verification

- `dotnet build StreamsPlayer.sln -c Release` succeeds.
- `grep -c "PLAYBACK SESSION\|AUDIO SESSION" src/StreamsPlayer.App` - expected 2 emitters.
- No `DispatcherTimer` count change: `grep -c "new DispatcherTimer" src/StreamsPlayer.App/PlayerWindow.xaml.cs`
  before and after are equal.
- Observation is phase 09 criterion 12 (a real unstable stream, both audio and video).

## Checks

- Status: Implemented.
- expected: Release build clean | actual: 0 warnings, 0 errors.
- expected: two session emitters | actual: `grep -c "PLAYBACK SESSION\|AUDIO SESSION" src/StreamsPlayer.App/*.cs` - one in `PlayerWindow.xaml.cs`, one in `MainWindow.Diagnostics.cs`.
- expected: no new timer on the playback path | actual: `new DispatcherTimer` count in `PlayerWindow.xaml.cs` unchanged at 3.
- **Gap found by observation, not by the plan**: `MainWindow_Closed` does not pass through the audio stop funnel, so quitting the app while a station played produced no `AUDIO SESSION` at all - the exact session a frustrated user would be reporting. `EndAudioSession()` is now called from `MainWindow_Closed` (logging and counters only; the stop path itself is untouched, since it manipulates UI elements of a closing window).
- Observed traces in `temp/SP-0040/`: `trace-video-live.log` (`outcome=live | legs=2 | reconnects=1` after a watchdog freeze), `trace-fail.log` (four bounded reconnects then `HardFail`), `trace-audio-real.log` (`outcome=live | ttff_ms=2520`), `trace-audio-broken.log` (`outcome=failed | legs=5 | reconnects=4`).
- Not observed: the `FLYLEAF *` records. This machine has no FFmpeg natives in the app output, so `VideoBackendFactory` falls back to LibVLC and the alternate backend cannot run here at all.
