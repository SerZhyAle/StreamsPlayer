# SP-0021: Windows system media controls

**Status:** Approved

## Goal

Expose active audio playback through Windows media controls and hardware media keys so listeners can control StreamPlayer while another application has focus.

## Why

System-level control is expected for desktop audio and removes the need to return to the catalog window for common actions.

## Non-goals

- Register arbitrary global keyboard shortcuts.
- Control video/RTSP windows or mix multiple audio sessions.
- Add lock-screen artwork downloads, cloud metadata, or OS playback history.
- Add seek controls to live streams.

## Constraints

- The system session represents only the one active inline audio stream.
- Play/Pause toggles live playback semantics: Pause stops the active live session; Play starts the same saved channel again at the live edge.
- Stop ends playback. Previous/Next follows the stable order of the view or named collection from which playback was started, skipping hidden or missing rows and without wrapping unless explicitly enabled later.
- Mute uses the existing persisted audio preference and does not change system master volume.
- The system title shows the station and, when available from SP-0014, current ICY text; stale text is cleared with playback.

## Acceptance criteria

1. Starting inline audio publishes one Windows media session with the correct play state and station title.
2. Hardware/system Play/Pause, Stop, and Mute produce the same state transitions as their in-app equivalents.
3. Previous/Next moves through the captured launch context, skips unavailable rows, stops cleanly at an end, and never starts hidden content.
4. Stopping audio, closing the app, or terminal playback failure clears the active system session and stale metadata.
5. Ordinary keyboard input in StreamPlayer and other applications is not intercepted beyond standard media-key handling.
6. State-transition checks and a real Windows media-key/system-overlay observation pass.

## Risks

Live streams have no meaningful paused position, so restart-at-live-edge must be communicated consistently. Windows media integration can outlive UI state if lifecycle cleanup is incomplete.

## Dependencies

SP-0014 supplies optional now-playing text. SP-0017 supplies named-collection navigation; neither blocks basic controls.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md).
