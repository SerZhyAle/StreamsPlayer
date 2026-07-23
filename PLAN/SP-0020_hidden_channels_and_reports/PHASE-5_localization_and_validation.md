# PHASE-5 — Localization consolidation, full check, and GUI observation

**Consumes:** Phases 1–4.
**Produces:** verified, release-parity implementation with observed GUI evidence. Closes AC6 and the run-and-observe half of every user-facing criterion.

## Steps

1. **Localization parity sweep** — confirm every key added in Phases 3–4 exists in **both** `Localization.en.xaml` and `Localization.ru.xaml` with no orphan/missing key. Static check: equal `x:Key` counts for the new keys; no `LocalizationService.Get`/`Format` call in the new code references a key absent from either dictionary.

2. **Release-parity gate** — `./scripts/check.ps1` (Release restore + build + `dotnet test`).
   - `expected: Release build clean, all Core tests pass (>= existing 38 + new Phase-1 tests) | actual: ...`.

3. **GUI run-and-observe** (per VALIDATION ladder level 7 — a build is not proof a GUI action works). Run the app and exercise each changed path; record `expected | actual` for each:
   - a. Video/RTSP stream that fails → actionable dialog appears (Retry / Remove / Copy report / Keep), not a bare OK.
   - b. Catalog stream → **Remove** hides it; it disappears from list/Grid, and **stays hidden after an explicit catalog refresh** and after restart.
   - c. Manual stream → **Remove** deletes it; it does not reappear after refresh.
   - d. Audio stream failure → same actionable dialog; Retry re-attempts.
   - e. **Copy report** → clipboard holds the localized bounded report with app version/UTC/title/kind/category and a **redacted** URL (no credentials, no local paths); nothing is sent.
   - f. Manage-hidden window lists hidden channels and **Unhide** restores them with pin/order intact; Keep/Close and Cancel change nothing.
   - g. Language switch EN⇄RU re-localizes the dialog and manage-hidden window.

   > Per SP-0012 precedent, if a user-owned Debug StreamsPlayer process is active and must not be interrupted, mark the GUI items `BlockNeedUserTest` with the exact steps rather than killing the user's process. `Verified` requires these observed.

## Static verification predicate

`./scripts/check.ps1` passes (Release build + all tests), localization parity holds, and every GUI item in step 3 is recorded as observed `expected | actual` — or the ticket is set `BlockNeedUserTest` listing the unobserved items. Do not set `Verified` while any manual item is unobserved.

## Result — automated DONE, GUI BLOCKED

- Localization parity: expected: en keys == ru keys, no one-sided/dup | actual: 176 == 176, none one-sided, no duplicates.
- Release-parity gate: expected: `scripts/check.ps1` clean build + all tests pass | actual: build 0 warnings / 0 errors; 56/56 tests passed (18 new Phase-1 tests).
- GUI run-and-observe (step 3 a–g): **BlockNeedUserTest** — a user-owned StreamsPlayer instance (PID 6492) is running and writes the same `catalog-state.json`; launching a second instance could clobber the new hide/delete saves, so it was not started (SP-0012 precedent). Items a–g remain unobserved and are listed in the ticket for the user to run.
