# PHASE-4 — Localization, docs, and grid-preview isolation

**Produces:** EN/RU Reconnecting strings; README/tuning-doc note on adaptive recovery; verified
grid-preview isolation. **Consumes:** Phase 2, Phase 3 (string keys they reference).

## Steps

1. **New localized strings** in `src/StreamsPlayer.App/Localization.en.xaml` and `Localization.ru.xaml`
   (keep keys identical across both; no emoji):
   - `ReconnectingAttempt` — EN `Reconnecting… (attempt {0} of {1})`; RU `Переподключение… (попытка {0} из {1})`.
   - `ReconnectingAudioAttempt` — EN `Reconnecting to {0}… (attempt {1} of {2})`;
     RU `Переподключение к {0}… (попытка {1} из {2})`.
   - Reuse existing `BufferingProgress`, `PlayingLive`, `PlayerUnavailable`, failure-dialog keys.
   - Static check: grep both dictionaries contain each new key; `dotnet build` (XAML compiles).

2. **Verify every referenced resource key exists** in both dictionaries (Phase 2/3 references vs keys):
   - Static check: for each key used via `SetResourceReference`/`LocalizationService.Format` in the
     changed files, confirm it is present in EN and RU. Expected: zero missing.

3. **Grid-preview isolation.** Confirm the preview capture path
   (`GridPreviewCoordinator`, `VideoFrameCaptureService`) neither invokes `RecoverAsync`/policy nor calls
   `RecordPlayOutcome` (only its own `PREVIEW FAIL` log + unreachable-tile mark). No foreground recovery,
   no red/fail play mark from a preview failure (constraint + Part C invariant 3).
   - Static check: grep shows no `RecordPlayOutcome`/`LivePlaybackRecoveryPolicy`/`PlaybackFailureDialog`
     reference in the preview files. Expected: none. (No code change expected; add a guard only if found.)

4. **Docs.** Update `README.md` line noting "adaptive recovery … remain later milestones" to reflect that
   bounded live recovery now ships. Add a short note to `docs/stream-playback-recommendations.md` cross-
   referencing the Part D policy now implemented (optional, concise). Mirror the README sentence into
   `README.ru.md` / `README.uk.md` only if the touched sentence exists there.
   - Static check: `rg -n "adaptive recovery" README*.md` reflects the shipped state.

## Phase static check

`dotnet build StreamsPlayer.sln -c Release` — expected: succeeds; all new resource keys resolve in EN+RU.
