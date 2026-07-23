# PHASE-5 — Validation and documentation

**Produces:** build/test evidence, the run-and-observe acceptance matrix, tuning/README notes.
**Consumes:** Phases 1–4.
**Goal (AC 2, AC 3, AC 4, AC 5, AC 8):** prove the switch, per-engine playback, and persistence with
run-and-observe evidence — not merely a green build — and record the payload-size impact.

## Steps

1. **Release-parity gate.** Run `./scripts/check.ps1` (Release restore + build + `dotnet test`).
   **expected:** build succeeds; all tests pass (Core count = prior + new backend-state test(s)) |
   **actual:** _record._

2. **Run-and-observe acceptance matrix.** Launch the app and record `expected: X | actual: Y` for:
   - **LibVLC default (AC 1/AC 3):** fresh/default state → Settings shows VLC pre-selected + labelled
     default; a video + an RTSP stream play exactly as before the refactor (fullscreen, volume/mute,
     tracks, save-frame thumbnail, recovery on a forced failure).
   - **Switch to Flyleaf (AC 2):** select FlyleafLib, open a stream → plays via Flyleaf; include a
     stream known troublesome on LibVLC and note the outcome.
   - **Flyleaf coverage (AC 4):** ≥1 HLS-live and ≥1 RTSP play; from `Current.log`/observation list
     which resilience behaviours reproduce and which do not; confirm any shortfall shows as the
     experimental label, not a crash or silent freeze.
   - **Switch back (AC 3):** re-select VLC → today's exact behaviour restored.
   - **Persistence (AC 5):** restart → last-selected engine still applied and shown.

3. **Payload-size check (risk).** Compare the published output size before vs after (both native
   stacks now ship): `./build.ps1 -Deploy:$false -Configuration Release` then inspect the publish
   folder / single-file EXE size. Record delta and confirm it is acceptable. **expected:** growth
   bounded to the added FFmpeg stack; noted | **actual:** _record._

4. **Docs.** Add a short note to `docs/stream-playback-recommendations.md` that the tuning baseline
   is the LibVLC default and that FlyleafLib is a selectable experimental fallback whose parity is
   tracked here (enumerate the gaps found in step 2). If the engine choice is user-visible enough to
   warrant it, add one line to `README.md` (and the RU/UK mirrors) under settings/features. No emoji.

5. **Set ticket status from reality.** Update the strategic ticket and this INDEX: `Implemented`
   if all ACs pass; `Partial` with the specific gap if Flyleaf coverage is short of an AC;
   `BlockNeedUserTest` for any acceptance item that could not be observed in this run.

## Static check

- `./scripts/check.ps1` green (step 1) and the acceptance matrix (step 2) fully recorded with
  `expected/actual`, no unobserved manual criterion left silently passed.
