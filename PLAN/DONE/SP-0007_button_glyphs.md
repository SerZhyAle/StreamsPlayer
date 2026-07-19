# SP-0007 — Button glyphs

**Status:** Verified

## Goal

Give every application button a deliberate, consistent glyph image so actions
are easier to recognise and no button relies on plain text or emoji-like
symbols.

## Non-goals

- Change the behaviour, shortcut, accessibility semantics, or localization of
  an action.
- Add a third-party icon package or a raster asset pipeline.
- Redesign non-button controls or product documentation beyond removing emoji.

## Constraints

- Glyphs are local vector images that remain crisp at Windows DPI settings.
- Labels, tooltips, and automation names remain available for clarity and
  accessibility.
- The application and documentation contain no emoji or Unicode icon
  characters.

## Acceptance criteria

1. Every WPF `Button` has a meaningful glyph image appropriate to its action.
2. The same action uses the same glyph across windows and states; pin has clear
   pinned and unpinned variants.
3. Existing actions, localization, tooltips, automation names, enabled state,
   and keyboard defaults continue to work.
4. No emoji or icon-font symbol remains in application or documentation source.
5. The solution builds, tests, and its changed UI is observed running.

## Risks

An icon-only compact control could lose discoverability. Existing tooltips and
automation names are retained, and ordinary controls keep their text labels.

## Last Audit

- PASS — every WPF button uses a shared vector glyph style or the dynamic pin
  glyph; ordinary controls retain localized text, compact controls retain
  tooltips and automation names.
- PASS — expected: no emoji or icon-font symbols in app or documentation |
  actual: source scan returned no matches.
- PASS — expected: Release build and tests succeed | actual: build completed
  with 0 warnings/errors; `dotnet test --no-build` passed 28/28.
- PASS — expected: changed UI renders and actions remain operable | actual:
  main-window screenshot shows ordinary and compact glyph controls; existing
  UI automation observed the Russian video player, fullscreen toggle, Escape,
  and F11 paths; evidence under `tmp/`.
