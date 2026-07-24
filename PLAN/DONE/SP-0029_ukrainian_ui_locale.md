# SP-0029 - Ukrainian UI locale

**Status:** Verified

## Problem / Why

The product ships Ukrainian on its outward surfaces - the GitHub Pages site and
`README.uk.md` - but the in-app UI offers only English and Russian
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
- Every key must exist in all three dictionaries - an absent key would fall back
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

- Missing UK keys surface as raw resource keys at runtime, not at compile time -
  needs a parity check of the three dictionaries.
- Turning a boolean toggle into a three-state control can break the persisted
  round-trip if the cycle logic mishandles the new enum value.
- Longer Ukrainian captions may crowd controls tuned for EN/RU width.

## Open questions

1. **Language control shape** - RESOLVED (owner, 2026-07-23): use a
   **dropdown/menu picker** rather than a cycling button, so the control scales if
   more locales are added later. The tactical plan replaces the current EN↔RU
   toggle with the picker.
2. **String source** - reuse the `README.uk.md` / site Ukrainian copy as the
   translation base, or translate fresh from the English keys?

## Implementation notes (SP-0029)

- `StreamsPlayer.Core/Models.cs` - `AppLanguage.Ukrainian` appended; persistence is unchanged
  (`JsonStringEnumConverter`), so an EN/RU state file still round-trips.
- `LocalizationService` - dictionary and culture resolved per language (`en`/`ru`/`uk`, `uk-UA`),
  plus `Available`, `NativeName` (read from the dictionaries, identical in all three) and `ShortCode`.
- `Localization.uk.xaml` (new) - full parity with the English key set; terminology follows
  `README.uk.md` and the site.
- `MainWindow.xaml` / `MainWindow.Localization.cs` - the EN<->RU toggle became a picker: the toolbar
  button shows the active short code and opens a three-item checkable menu (Decision 1).
- `ProductInfo.InstructionsUrl` - Ukrainian now points at `README.uk.md` instead of the English one.
- The stale `SwitchLanguageTip`/`SwitchLanguageName` strings ("switch between Russian and English")
  were removed; the picker uses `LanguagePickerName`.

Static checks: `dotnet build StreamsPlayer.sln -c Debug` -> expected 0 errors | actual 0 errors,
0 warnings. Dictionary parity -> expected identical key counts | actual en 289 / ru 289 / uk 289.

## Verification - agent-driven UIA run (2026-07-24)

- expected: the picker lists three languages in their own names | actual: `English`, `Русский`,
  `Українська` with the active one checked.
- expected: choosing Ukrainian relabels the whole interface | actual: title `Трансляції`, subtitle
  `Незалежний каталог live-ТБ, радіо та RTSP`, toolbar `Список / Сітка / Історія / Налаштування /
  Додати потік / Оновити каталог`, filters `Медіа / Категорія / Мова / Країна / Мін. бітрейт /
  Сортування / Добірка`, status `0 з 0 каналів`, empty state `Потоків поки немає…` - no English and
  no resource-key leakage (`tmp/uia/shots/sp0029-ukrainian.png`).
- expected: the choice persists | actual: `CatalogState.Language == Ukrainian` after a restart, with
  the UI still Ukrainian and the picker button reading `UK`.
- expected: menus, dialogs and secondary windows follow | actual: row menu (`Відкрити`, `Закріпити`,
  `Додати до добірки`), sleep-timer menu (`15 хвилин`…), collections window (`Добірки`) all Ukrainian.
