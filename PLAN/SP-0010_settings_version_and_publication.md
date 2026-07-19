# SP-0010: Settings, calendar versioning, and publication copy

**Status:** BlockExternal

**Block:** The intended GitHub/Pages URLs are not published and this machine has no `makeappx.exe`; re-audit after the repository/Pages exist and the Windows SDK plus Partner Center identity are available.

## Goal

Give StreamPlayer a compact bilingual Settings window for infrequently changed preferences and product information, establish one calendar-based version contract, and maintain accurate submission-ready text for Microsoft Store and winget.

## Why

The main window should stay focused on browsing and playback. Grid presentation preferences, documentation links, authorship, and version information belong in a discoverable secondary surface. Release channels also need one unambiguous version and accurate metadata before submission.

## Product decisions

- Add a localized Settings action to the main command area.
- Keep the window compact and divide it into Grid and About sections rather than adding navigation or tabs.
- Offer Small, Medium, and Large 16:9 stream-tile sizes; Medium preserves the current presentation.
- Let users enable or disable automatic stream-thumbnail updates. Disabling the option stops live capture while retaining already cached thumbnails.
- Show the exact application version, author, localized GitHub instructions, project source, website, and privacy links.
- Use `YY.MMDD.HHmm` as the canonical product, tag, and winget version. MSIX appends the required fourth `.0` component.
- Provide English and Russian Store copy and winget locale metadata that describe only implemented behavior.

## Non-goals

- Add automatic application updates, startup-at-login, a media-backend selector, default volume, or destructive data-reset actions.
- Delete preview cache when automatic updates are disabled.
- Publish, upload, tag, reserve a Store identity, or submit a winget pull request.
- Promise that external certification will pass without Partner Center identity, package, screenshot, and policy review.

## Constraints

- Settings persist in the existing local catalog state and old state files keep safe defaults.
- The window follows the active English/Russian interface and all links open through the Windows default browser.
- Tile-size changes apply without restarting and preserve the 16:9 grid layout.
- Preview capture remains explicit and lifecycle-safe when disabled or re-enabled.
- The displayed version must come from assembly build metadata, not a duplicated UI literal.
- Store and winget copy must include privacy, support, licensing, system-requirement, and release-note guidance without overstating playback compatibility.

## Acceptance criteria

1. A localized glyph-backed Settings button opens one compact modal window.
2. Small, Medium, and Large tile sizes and the automatic-thumbnail preference save, reload, and apply to the active grid without restart.
3. Turning automatic thumbnails off stops preview work; turning it back on restarts previews only when Grid mode is active.
4. About shows the canonical `YY.MMDD.HHmm` version, Serhii Zhyhunenko / SerZhyAle authorship, and working localized instruction, source, website, privacy, and author links.
5. Repository rules, build properties, GitHub release validation, MSIX packaging, release checklist, and winget templates use the same version contract; MSIX alone uses `YY.MMDD.HHmm.0`.
6. Microsoft Store publication copy exists in English and Russian with descriptions, feature lists, keywords, system requirements, certification notes, and screenshot guidance within documented limits.
7. Winget templates use the supported schema, include English and Russian metadata, privacy/support/release URLs, and document validation/submission steps.
8. State tests, Release build/tests, version inspection, static metadata checks, and GUI observation pass with no open manual criterion.

## Risks

- Treating the product version as ordinary semantic versioning can strip leading zeroes; informational/file version metadata must preserve the display form.
- A preview preference change during window activation can race coordinator start/stop unless MainWindow owns the transition.
- Store certification depends on external Partner Center declarations and supplied imagery, so repository text can be submission-ready but cannot guarantee approval.

## Research

See [research dossier](SP-0010_settings_version_and_publication/research.md).

## Implementation

- Added a bilingual glyph-backed Settings window with persisted 16:9 tile sizes, automatic-thumbnail control, version/authorship details, and localized instruction/project/privacy links.
- Integrated tile density and preview coordinator lifecycle with the saved settings and preserved safe defaults for existing state files.
- Established UTC `YY.MMDD.HHmm` versioning across assembly metadata, release tags, release validation, MSIX conversion, repository rules, and packaging guidance.
- Replaced the Store copy deck with English/Russian submission text and upgraded four-file winget templates to schema 1.12.0 with a Russian locale.

## Last audit

**Date:** 2026-07-19

- expected: complete bilingual resource contract | actual: 146 English and 146 Russian keys, zero differences or duplicates.
- expected: compact Settings with all requested information | actual: observed 510 x 570 logical pixels; version, author, both languages, and five required link actions visible.
- expected: all tile sizes and preview preference persist/apply | actual: Large and Small applied, Small survived restart, Medium restored, and disabling previews hid the refresh action; Core round-trip test passed.
- expected: one exact canonical build version | actual: informational/file version `26.0719.0131`; all four build properties match; internal CLR version safely normalizes to `26.719.131.0`.
- expected: Store copy stays inside official limits | actual: largest text block 1,266 of 10,000 characters and longest feature 71 of 200 characters; required EN/RU sections and certification guidance present.
- expected: filled winget templates validate | actual: `winget validate` 1.12.0 passed without warnings after placeholder substitution.
- expected: Release build/tests pass | actual: `scripts/check.ps1` passed 37/37 tests with zero warnings and zero errors.
- expected: MSIX package builds locally | actual: blocked before mutation because the Windows SDK `makeappx.exe` is not installed.
- expected: external Settings links resolve | actual: author profile resolves; intended repository, instructions, and Pages URLs return 404 until first publication.

Audit result: implementation PASS; publication verification remains BlockExternal for the documented package/tooling and public-endpoint dependencies.
