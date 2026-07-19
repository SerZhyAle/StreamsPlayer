# SP-0011: Compact catalog controls and remembered browsing session

**Status:** BlockNeedUserTest — restart restoration must be observed without the user's already-running StreamPlayer instance sharing the local state file.

## Goal

Make the catalog header and controls consume less vertical space, and restore a person's browsing context when StreamPlayer reopens.

## Why

The current header separates its title, description, and action controls into multiple lines. Search and filters similarly use two vertical bands even though a short query is normally sufficient. Reopening the app loses the person's filters, sort choice, and place in a large catalog.

## Non-goals

- Change catalog data, refresh timing, merge rules, playback, or pinning behaviour.
- Add automatic catalog downloads or a remote account/session.
- Change the card or Grid-preview visual design.

## Constraints

- Keep visible labels, keyboard access, localized English/Russian UI, and the existing responsive catalog behavior.
- Persist only in the existing local catalog state, atomically and without recording network data.
- Restore the browsing position relative to a stable channel identity so catalog reordering and window-size changes do not make a saved pixel offset incorrect.
- A missing or filtered-out saved channel must fall back safely to the beginning of the result list.

## Acceptance criteria

1. The title and localized description share one header line; all header actions share a right-aligned line.
2. Search, Clear, media/category/language/country filters, and sort choice share one controls line with visible labels.
3. Search query, all filter values, and sort choice are restored on a later launch.
4. The catalog restores the previously visible list position when its saved anchor remains in the restored result set; otherwise it opens at the top.
5. Existing local state remains loadable, and the unchanged Core catalog contract, build, tests, and a real WPF observation pass.

## Risks

Header and controls must remain usable at the supported narrow width. The scroll anchor must be restored only after filtering and responsive rows are realized.

## Research

See [research dossier](SP-0011_compact_catalog_controls_and_session/research.md).

## Last Audit

- PASS — title/subtitle and header actions each occupy one line; search, Clear, and every labelled selector occupy one line. At the 820×560 logical minimum size, UI Automation observed all six controls in the same horizontal band and no header/control bounds outside the window.
- PASS — browsing-session fields are value-only Core data and `Save_PreservesCatalogBrowsingSession` round-trips query, all selector values, sort, and the anchor GUID.
- PASS — the App restores controls after localized options/facets, records a first-visible-channel GUID on scrolling, and delays scroll restoration until the result rows render; unavailable anchors are safely ignored.
- PASS — expected: Release build and Core tests succeed | actual: build completed with 0 warnings/errors; 38/38 tests passed.
- MANUAL — expected: query, all filters/sort, and a scrolled list position survive a complete close/reopen | actual: not run because a user-owned Debug StreamPlayer process is active and shares `%LOCALAPPDATA%\\StreamPlayer`; it was left untouched to avoid modifying its session.
