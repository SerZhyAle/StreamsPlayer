# SP-0021: Windows system media controls

**Status:** BlockNeedUserTest — code complete and building; awaiting a real Windows media-key/overlay observation (AC 6). Exit: user runs the app with the setting on and confirms the flyout + media keys, then status advances to Verified or Partial.

## Goal

Expose active audio playback through Windows media controls and hardware media keys so listeners can control StreamsPlayer while another application has focus.

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
5. Ordinary keyboard input in StreamsPlayer and other applications is not intercepted beyond standard media-key handling.
6. State-transition checks and a real Windows media-key/system-overlay observation pass.

## Risks

Live streams have no meaningful paused position, so restart-at-live-edge must be communicated consistently. Windows media integration can outlive UI state if lifecycle cleanup is incomplete.

## Dependencies

SP-0014 supplies optional now-playing text. SP-0017 supplies named-collection navigation; neither blocks basic controls.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md).

## Implementation (2026-07-22)

Opt-in per user direction: a new **Show system media controls** checkbox in Settings → Playback,
default **off**, so the app behaves exactly as before unless enabled (`CatalogState.SystemMediaControls`,
default `false`).

- **WinRT reachability.** App TFM bumped `net10.0-windows` → `net10.0-windows10.0.19041.0` (with
  `SupportedOSPlatformVersion` 10.0.17763.0) to project the SMTC APIs. Core/tests/harness unchanged.
- **SMTC wrapper.** [SystemMediaControls.cs](../src/StreamsPlayer.App/SystemMediaControls.cs) contains all
  WinRT usage: a source-less `Windows.Media.Playback.MediaPlayer` yields a real SMTC in a Win32/WPF process
  (its `CommandManager` is disabled; state driven manually). Button presses marshal to the UI
  `SynchronizationContext` and surface as a plain `Command` event. `TryCreate()` returns `null` on
  unsupported hosts, degrading to ordinary behaviour.
- **Glue.** [MainWindow.SystemMedia.cs](../src/StreamsPlayer.App/MainWindow.SystemMedia.cs) publishes the
  session on audio start, mirrors ICY track text (SP-0014) into the title, and clears on stop / terminal
  fail / window close / feature-disable.
- **Play/Pause semantics.** Pause stops the live session but keeps a Paused system session so a later Play
  restarts the same channel at the live edge (`_audioPausedChannel`, `StopAudioPlayback(clearSystemSession:)`).
- **Previous/Next.** A launch context is captured at play time (the filtered view's stable audio order);
  navigation uses the pure, tested [LivePlaybackNavigation](../src/StreamsPlayer.Core/LivePlaybackNavigation.cs)
  (skips hidden/missing rows, no wrap, stops cleanly at ends).

### Scoping deviation — Mute (AC 2)

**Not implemented.** The Windows SMTC button set has no Mute button, and hardware mute keys drive the OS
master volume — which the ticket's own constraints forbid this feature from changing. There is also no
existing in-app audio mute to mirror. Mapping "Mute" onto this control surface needs a product decision
(e.g. add an in-app audio mute button first); flagged rather than silently faked.

### Verification

- `dotnet build StreamsPlayer.sln -c Debug` — expected: succeeds | actual: succeeds, 0 warnings (WinRT
  projection resolved).
- `dotnet test StreamsPlayer.sln` — expected: all pass | actual: 145 passed (incl. 9 new
  `LivePlaybackNavigationTests`).
- Manual media-key / system-overlay observation (AC 6) — **pending user run** (see Status).
