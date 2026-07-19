# SP-0008 — Launch a stream and resume selection

**Status:** Verified

## Goal

Let Windows users launch StreamPlayer directly for a stream URL or local
channel identifier, create reusable per-channel launch entries, and continue
with their latest selected local stream on an ordinary launch.

## Non-goals

- Add background catalog refreshes, a protocol file association, or a global
  single-instance handoff.
- Persist externally supplied URLs as catalog records.
- Change audio/video routing or media backend capability.

## Constraints

- Catalog records are addressed by their persisted GUID, not a display index.
- Explicit launch arguments take precedence over automatic resume.
- An invalid/missing target fails softly and leaves the main UI usable.
- Desktop integration remains local to the user and creates no release asset.

## Acceptance criteria

1. `--url` plays a supported HTTP(S) or RTSP URL without catalog refresh or
   persistent catalog insertion.
2. `--id` plays the matching persisted channel; an invalid GUID or missing
   record reports a clear localized status without a crash.
3. A selected local channel offers Copy command and Create desktop shortcut;
   the generated command uses its GUID.
4. Selecting a local channel persists it. Launching without arguments resumes
   that channel through the existing playback route.
5. Explicit launch, resume, persistence, localization, build/tests, and a
   running UI path are verified.

## Risks

Automatic resume can trigger a network play attempt at launch. It happens only
after the user has previously selected the channel, uses the existing offline
gate, and never refreshes the catalog.

## Last Audit

- PASS — `--url` and `--id` parse as explicit launch requests; invalid input
  has a localized soft-failure status. Empty arguments only consult the saved
  selected GUID.
- PASS — selecting a card wrote `LastSelectedChannelId` to local state; an
  observed `--id` launch rendered the matching channel in a responsive main
  window without a catalog refresh.
- PASS — after selecting a card, Settings opened and its Copy command and
  Create desktop shortcut actions were both enabled in UI Automation.
- PASS — expected: Release solution build and tests succeed | actual: build
  finished with 0 warnings/errors; 37/37 tests passed.
