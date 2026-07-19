# SP-0009 — Grid tile action menu

**Status:** Verified

## Goal

Make the Grid tile overflow menu a complete action surface for opening,
fullscreen opening, desktop shortcut creation, editing, and pinning.

## Constraints

- Open reuses current audio/video routing; fullscreen applies only to video and
  RTSP streams.
- Desktop shortcut creation uses the existing stable channel-GUID command.
- Edit is available only for manually added streams; catalog and imported rows
  must remain protected from modification.
- Menu text is localized in English and Russian; no emoji is introduced.

## Acceptance criteria

1. Grid overflow offers Open, Open fullscreen, Create desktop shortcut, Edit,
   and Pin/Unpin in a clear order.
2. Open and fullscreen retain existing playback and outcome handling.
3. Shortcut creation creates the same GUID-based shortcut as Settings.
4. Editing validates a manual stream URL, avoids duplicate URLs, persists its
   new fields, and preserves its identity and user state.
5. Disabled actions explain unsupported stream types/origins through their
   menu state; build/tests and a running Grid menu are verified.

## Last Audit

- PASS — Grid overflow observed through UI Automation with the localized menu:
  Open, Open fullscreen, Create desktop shortcut, Edit, and Unpin.
- PASS — fullscreen is disabled for audio; Edit is disabled for non-manual
  streams; manual edit validates URL and preserves the stored record identity.
- PASS — shortcut creation is shared with Settings and generates the persisted
  GUID argument rather than an ordering value.
- PASS — expected: Release solution build and tests succeed | actual: build
  completed with 0 warnings/errors; 37/37 tests passed.
