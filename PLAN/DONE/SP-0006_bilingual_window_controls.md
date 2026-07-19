# SP-0006: Bilingual interface and window controls

**Status:** Verified

## Goal

Let people switch the complete StreamsPlayer interface between English and Russian from the top of the main window, keep either main or video window above other applications when requested, and watch video in a decoration-free fullscreen mode.

## Why

The current English-only interface is inaccessible to Russian-speaking users, and video viewing lacks the basic window controls needed for monitoring and focused playback.

## Non-goals

- Translate catalog-provided channel titles, topics, languages, countries, or URLs.
- Add languages other than English and Russian.
- Replace either media backend or add playback controls unrelated to window state.
- Force the main and player windows to share one always-on-top value.

## Constraints

- A compact `RU / EN` button and main-window always-on-top checkbox stay at the top of the main window.
- Language, main-window topmost, and player-window topmost preferences survive restart.
- Existing open windows update their static labels when language changes; new dialogs open in the selected language.
- Fullscreen hides the title bar and other system decorations, fills the current monitor, and restores the prior window state on exit.
- F11 toggles fullscreen and Escape exits it. Closing from fullscreen remains safe.
- Preserve catalog, grid-preview, playback, and explicit-refresh behavior.

## Acceptance criteria

1. The top `RU / EN` button switches all static main-window text, tooltips, accessible names, statuses, and messages between English and Russian without restarting.
2. Add-stream and video-player windows use the selected language, and an already-open player updates its static controls after a language switch.
3. The main-window always-on-top checkbox immediately controls only the main window and persists across restart.
4. The video-player always-on-top checkbox immediately controls that player; its value becomes the default for subsequently opened players and persists across restart.
5. The video player has a Fullscreen action; fullscreen removes system decorations and fills the monitor, while its action, F11, and Escape restore the prior window style/state.
6. Existing catalog browsing, grid previews, pinning, catalog refresh, audio playback, video playback, and player error handling remain intact.
7. Release build/tests pass and GUI observation proves both languages, both topmost properties, and fullscreen enter/exit.

## Risks

- Replacing resource dictionaries at runtime can leave code-assigned status text stale unless those paths are explicitly refreshed.
- WPF fullscreen transitions can restore to the wrong maximized/normal state if window chrome values are not captured before mutation.

## Research

See [research dossier](SP-0006_bilingual_window_controls/research.md).

## Implementation

- Added matching English and Russian WPF resource dictionaries with live switching for the main window, add dialog, player, filters, statuses, messages, tooltips, and accessible names.
- Persisted the selected language plus independent main-window and player-window topmost defaults in `CatalogState`.
- Added the top `RU / EN` action, both topmost checkboxes, and borderless player fullscreen with button, F11, and Escape paths.
- Documented the user-visible controls in all repository README variants.

## Last audit

**Date:** 2026-07-19

- expected: English and Russian dictionaries have the same contract | actual: 99 keys in each, zero differences.
- expected: non-default language and window preferences survive storage | actual: focused round-trip test passed and GUI restart restored Russian with both topmost values enabled.
- expected: main and player topmost remain independent | actual: Win32 style observation found the player topmost while the main window was not topmost.
- expected: fullscreen fills the active monitor without system chrome and restores safely | actual: observed 3840 x 2160 with no caption; button/Escape and both F11 transitions restored the caption.
- expected: an open player follows a live language switch | actual: its controls changed from Russian to English without reopening.
- expected: release validation remains clean | actual: `scripts/check.ps1` passed 28 tests with zero warnings and zero errors.

Audit result: PASS. All acceptance criteria are satisfied; no corrective action remains.
