# PHASE-5 — Validation

**Produces:** static-check evidence and run-and-observe PASS/FAIL for AC 6. **Consumes:** Phases 1–4.

## Static checks

1. `./scripts/check.ps1` (Release restore + build + `dotnet test`).
   - expected: build succeeds; all tests pass (existing + new `LivePlaybackRecoveryPolicyTests`).
2. Policy tests in isolation:
   `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~LivePlaybackRecoveryPolicyTests"`.
   - expected: all pass.

## Run-and-observe (GUI — a build is not enough for AC 6)

Launch: `./build.ps1 -Run` (or `dotnet run --project src/StreamsPlayer.App`). Read `Current.log`
(`%LOCALAPPDATA%\StreamsPlayer\Current.log`) for the recovery events. Record each as
`expected: … | actual: …`:

1. **Successful recovery** — play a flaky/looping HLS video; on an interruption observe the wait label
   switch to `Reconnecting… (attempt N of M)` then return to live; log shows `PLAYBACK OPEN reason=recover`
   followed by `PLAYBACK LIVE`, and the policy counter resets on live.
2. **Cancellation** — during an active Reconnecting backoff, (a) close the player window and (b) in a
   second run select another stream / stop audio; the old stream does not restart, no post-cancel
   `PLAYBACK OPEN` appears (AC 4).
3. **Terminal failure** — play a permanently dead URL (or one returning a non-429 4xx); after budget
   exhaustion (or immediate HardFail) the `PlaybackFailureDialog` shows Retry + Copy + Hide/Delete + Keep,
   and the transient budget is not spent on a non-429 4xx (AC 2, AC 5).
4. **Buffering vs Reconnecting distinct** — confirm plain buffering shows `Buffering… {0}%` while recovery
   shows `Reconnecting…`, in both EN and RU (toggle language) (AC 3).
5. **Grid isolation** — in Grid mode with a failing tile, confirm no failure dialog, no recovery, and no
   red status bullet from the preview path.

If live streams cannot be exercised in this environment, mark the ticket `BlockNeedUserTest` with the
exact steps above rather than claiming Verified.

## Exit

Update the strategic ticket status from reality (Implemented, or BlockNeedUserTest for the GUI items),
per `docs/agent/SPEC_LIFECYCLE.md`. Full audit runs under `$streamsplayer-spec-check`.
