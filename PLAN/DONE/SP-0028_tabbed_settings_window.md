# SP-0028 - Tabbed Settings Window

**Status:** Verified

## Resolved decisions

1. **Tab orientation** - vertical left rail (`TabStripPlacement="Left"`); long
   Russian captions (e.g. "Воспроизведение") would crowd a top strip.
2. **Icons** - reused the existing monochrome glyph vocabulary as `Path`
   geometries; a shared `TabIcon` style keeps them consistent.
3. **Shortcuts** - kept as its own tab.
4. Captions reuse existing localised strings (`GridSettings`,
   `LaunchShortcuts`, `PlaybackSettings`, `PlaylistPortability`, `About`); no new
   strings were required.
5. The two Playback checkboxes wrap their labels (content is a wrapping
   `TextBlock`) and the window is 620 wide so no localised label clips.

## Problem / Why

The Settings window is a single fixed-height (510×720, non-resizable) column that
stacks every unrelated concern vertically: grid layout, per-stream launch
shortcuts, playback behaviour, playlist import/export, and product/About
information. As settings accumulate the window has no room to grow gracefully,
related controls are hard to find, and the visual hierarchy relies on stacked
group boxes. The user wants a clearer, scalable structure.

## Goal

Reorganise the Settings window into a **tabbed** layout. Each tab is identified
by a single-colour (monochrome glyph) icon plus a caption, and groups controls
by meaning. No existing setting, action, or About element may be lost, and every
current behaviour must continue to work exactly as before.

## Proposed grouping (what each tab contains)

Every control currently in the window must land in exactly one tab:

1. **Grid / Layout** - stream tile size selector; "Update stream previews"
   toggle.
2. **Shortcuts** - selected-stream label; "Copy launch command"; "Create
   desktop shortcut". (These act on the currently selected stream and are
   conceptually distinct from grid appearance.)
3. **Playback** - "Keep awake during playback"; "System media controls".
4. **Import / Export** - portability hint; import from file; import from URL;
   export all; export pinned.
5. **About** - product name, version, author, and all documentation/source/
   website/privacy/author hyperlinks.

Grouping is a product decision, not a fixed contract; the tactical plan may
merge or split tabs (e.g. Grid + Shortcuts) if that reads better, provided the
"no lost functionality" rule holds.

## Non-goals

- No change to what any setting does or to how settings persist.
- No change to the catalog refresh model, the MANUAL/IMPORTED merge contract, or
  any Core code - this is an App-only WPF presentation change.
- No new settings introduced under cover of this work.
- No redesign of the individual controls, glyph button styles, or colour theme
  beyond what the tab shell requires.

## Constraints

- Save and Cancel remain a **single persistent footer** shared by all tabs (not
  duplicated per tab); switching tabs must not commit or discard pending edits.
- Tab captions and any new UI strings must be fully localised in both
  `Localization.en.xaml` and `Localization.ru.xaml`; no hard-coded English.
- Tab icons are **monochrome single-colour glyphs**, visually consistent with
  the existing glyph button vocabulary.
- Keep the window keyboard- and screen-reader-accessible: tabs reachable via
  keyboard, `AutomationProperties.Name` preserved on moved controls, Save still
  `IsDefault`.
- Window may keep a fixed size or adopt a size that comfortably fits the tab
  content; it must not clip any tab's controls.

## Acceptance criteria

- Opening Settings shows a tab strip; each tab has a monochrome icon and a
  localised caption.
- Every control listed above is present, reachable, and functional from its tab:
  tile size persists, previews toggle persists, keep-awake and system-media
  toggles persist, all four import/export actions work, both shortcut actions
  work against the selected stream, and every About link opens.
- Save persists all pending changes across all tabs in one action; Cancel
  discards them; both behave identically to today regardless of the active tab.
- Switching languages relabels every tab caption and control.
- Run-and-observe evidence captured for: at least one setting on each tab
  round-tripping through Save/reopen, and one import/export and one shortcut
  action.

## Risks

- Moving controls between containers can silently break event wiring or
  `x:Name` references in the code-behind - regressions are behavioural, not
  compile-time.
- Localised captions of differing length may crowd the tab strip.
- Fixed window height tuned for the old stacked layout may clip a busy tab.

## Open questions

1. **Icon source** - reuse/extend the existing glyph button style set, or
   introduce a small dedicated tab-icon set? (Recommendation: reuse the existing
   glyph vocabulary for visual consistency.)
2. **Shortcuts placement** - its own tab, or merged into Grid / Layout? (Depends
   on how empty a standalone Shortcuts tab feels.)
3. **Tab orientation** - horizontal top tabs vs. a vertical left rail. (A
   vertical rail scales better as tabs grow, but horizontal is the WPF default.)

## Verification - agent-driven UIA run (2026-07-24)

Driven headlessly through Windows UI Automation against a sandboxed `%LOCALAPPDATA%\StreamsPlayer`
(the real folder was renamed aside and restored afterwards); evidence PNGs under `tmp/uia/shots/`.
- expected: a five-tab vertical rail with icon plus localized caption | actual: Grid, Launch shortcuts, Playback,
  Playlists (M3U), About - localized in EN and RU (`Сетка`, `Ярлыки запуска`, `Воспроизведение`, `Плейлисты (M3U)`,
  `О программе`).
- expected: settings round-trip through Save | actual: tile size Large + automatic thumbnails off persisted to
  state and were shown again on reopen.
- expected: Cancel discards and switching tabs mid-edit commits nothing | actual: toggling previews and tile size
  then switching tabs left state untouched; Cancel discarded both edits.
- expected: shortcut and About actions work from their tabs | actual: `Copy command` produced
  `StreamsPlayer.exe --id "<guid>"` on the clipboard with the "copied" confirmation; the `Website` hyperlink
  launched the default browser with no error dialog.
- expected: import/export reachable from the Playlists tab | actual: covered by the SP-0016 run in the same session.

