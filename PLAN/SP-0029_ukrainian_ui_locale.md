# SP-0029 — Ukrainian UI locale

**Status:** Draft

## Problem / Why

The product ships Ukrainian on its outward surfaces — the GitHub Pages site and
`README.uk.md` — but the in-app UI offers only English and Russian
(`Localization.en.xaml` / `Localization.ru.xaml`, toggled by a single EN↔RU
button in [MainWindow.Localization.cs](src/StreamsPlayer.App/MainWindow.Localization.cs)).
A Ukrainian-speaking user who found the app through the localized website meets a
UI with no Ukrainian. The owner decided (spread-back session, 2026-07-23) to
close this per-surface gap so the app UI matches the site's EN+RU+UK coverage.

## Goal

Add Ukrainian as a first-class in-app UI language, at full string parity with the
existing English and Russian dictionaries, selectable and persisted like the
current two.

## Scope (what changes)

- **Core:** extend `AppLanguage` (currently `English`, `Russian` in
  [Models.cs](src/StreamsPlayer.Core/Models.cs)) with `Ukrainian`. Persistence is
  already enum-based via `CatalogState.Language` with `JsonStringEnumConverter`,
  so old and new state files round-trip.
- **App:** add `Localization.uk.xaml` with every key present in
  `Localization.en.xaml` / `Localization.ru.xaml` (no missing or hard-coded
  strings). Map `Ukrainian` → the `uk` dictionary and the `uk-UA` UI culture in
  `LocalizationService.Apply`.
- **Language control:** the current EN↔RU toggle must become a three-way
  selection (EN → RU → UK → EN cycle, or a small picker). The button/label must
  make the active and next language obvious.

## Non-goals

- No change to the catalog refresh model, the MANUAL/IMPORTED merge contract, or
  any data-flow contract.
- No new user-facing strings introduced under cover of this work beyond what a
  language selector needs; the UK dictionary translates the existing key set.
- No change to which locales the website, README, Store listing, or winget locale
  files ship (per-surface coverage is legitimate; this ticket only lifts the
  in-app UI to EN+RU+UK).

## Constraints

- The `uk` ISO code is used in code and state; any "UA" text is a display label
  only (matches the website convention).
- Every key must exist in all three dictionaries — an absent key would fall back
  to the resource key string, which is a visible defect.
- Keep the language control keyboard-accessible; preserve `AutomationProperties`.

## Acceptance criteria

- The app offers English, Russian, and Ukrainian; selecting Ukrainian relabels
  every window, filter option, status/now-playing string, and settings caption
  into Ukrainian with no English or resource-key leakage.
- The choice persists across restart (`CatalogState.Language == Ukrainian`) and
  an existing EN/RU state file still loads unchanged.
- Run-and-observe evidence: switch to UK, screenshot the main window and the
  Settings tabs, restart, confirm UK is restored.

## Risks

- Missing UK keys surface as raw resource keys at runtime, not at compile time —
  needs a parity check of the three dictionaries.
- Turning a boolean toggle into a three-state control can break the persisted
  round-trip if the cycle logic mishandles the new enum value.
- Longer Ukrainian captions may crowd controls tuned for EN/RU width.

## Open questions

1. **Language control shape** — RESOLVED (owner, 2026-07-23): use a
   **dropdown/menu picker** rather than a cycling button, so the control scales if
   more locales are added later. The tactical plan replaces the current EN↔RU
   toggle with the picker.
2. **String source** — reuse the `README.uk.md` / site Ukrainian copy as the
   translation base, or translate fresh from the English keys?
