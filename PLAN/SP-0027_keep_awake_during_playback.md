# SP-0027: Keep the computer awake during active playback

**Status:** Implemented

Tactical plan: [SP-0027_keep_awake_during_playback/INDEX.md](SP-0027_keep_awake_during_playback/INDEX.md)

## Implementation notes (SP-0027)

Phases 1-4 complete. Changes:
- `src/StreamsPlayer.Core/Models.cs` — `CatalogState.KeepAwakeDuringPlayback` (default `true`).
- `src/StreamsPlayer.App/WakeGuard.cs` (new) — ref-counted `SetThreadExecutionState` helper,
  separate system/display counters, `Enabled` gate, `Reset`.
- `MainWindow.xaml.cs` — audio session acquires system-only wake in `PlayChannelAsync`, releases in
  `StopAudioPlayback`; `WakeGuard.Enabled` set from state in `MainWindow_Loaded`.
- `PlayerWindow.xaml.cs` — video session acquires system+display wake in `PlayerWindow_Loaded`,
  releases in `PlayerWindow_Closed`.
- `SettingsWindow.xaml`/`.xaml.cs`, `MainWindow.Settings.cs` — localized toggle, persisted, applied
  immediately on change.
- `Localization.en.xaml`/`.ru.xaml` — `PlaybackSettings`, `KeepAwakeDuringPlayback`(+`Tip`).
- `App.xaml.cs` `OnExit` — `WakeGuard.Reset()` safety net.

Static checks: `dotnet test tests/StreamsPlayer.Core.Tests -c Debug` → 108/108 pass; App compiles
(no CS errors; full solution build was blocked only by a running app instance holding Core.dll).

## Verification — BlockNeedUserTest (Phase 5)

AC 7 needs run-and-observe of idle-sleep suppression, which requires a shortened Windows idle-sleep
timeout, real elapsed idle time, and closing the currently-running app instance. Pending user test:
on-state prevents sleep, off-state does not, and release-on-stop lets the machine sleep again.

## Goal

Add a user option — enabled by default — that stops Windows from going to sleep on its
idle timer while StreamsPlayer is actively playing a stream, so a long listening or viewing
session is not cut short by the machine sleeping.

## Why

Left idle, Windows sleeps on its own timer even while the app plays audio or live video,
unless the app explicitly asks to stay awake. A user who leaves a radio station or a live
video running can return to a slept machine and a dropped stream. Standard media players
hold a power request while they play; StreamsPlayer should do the same, on by default, with
a switch for users who prefer the system's normal sleep behaviour.

## Non-goals

- Do not wake the machine, cancel a scheduled sleep, or override user-initiated sleep,
  hibernate, lid-close policy, or the power button. Only the idle-sleep timer is affected.
- Do not keep the machine awake when nothing is playing — idle catalog browsing, or paused
  and stopped playback.
- Do not keep the machine awake for background grid-preview capture; previews are a browsing
  aid, not a playback session.
- No change to `StreamsPlayer.Core`: the wake behaviour is Windows-specific and lives in the
  app only. Core stays platform-neutral with no OS dependency.
- No new logging facade; no change to the explicit catalog-refresh contract or the
  MANUAL/IMPORTED merge protection.
- No coupling to the audio sleep timer (SP-0022) beyond both releasing the wake lock when
  playback stops.

## Decisions

1. **Default on.** A fresh install and any pre-existing state start with the option enabled.
2. **Held only during a real session.** The wake lock exists only while a genuine playback
   session is active — inline audio playing, or a video/RTSP player window playing — and is
   released the moment playback stops, pauses, the session ends, or the app exits.
3. **Audio vs video granularity.** An active video/RTSP session keeps both the system and the
   display awake (the user is watching). An audio-only session keeps the system awake but lets
   the display turn off normally (radio listening does not need the screen lit).
4. **Single localized switch.** The option is one on/off control in Settings, localized in
   English and Russian, no emoji, persisted across restart alongside existing preferences.

## Constraints

- Only the idle-sleep timer is suppressed; explicit user sleep, hibernate, lid-close policy,
  and the power button are never overridden.
- The lock must be released reliably on every stop path — normal stop, pause, session end,
  window close, app exit, and unhandled failure — so the machine is never left permanently
  unable to sleep.
- Toggling the option off while playing releases an active lock immediately; toggling it on
  while playing acquires one.
- The behaviour and its release live entirely in `StreamsPlayer.App`; Core gains no OS
  dependency and the App -> Core direction is unchanged.

## Acceptance criteria

1. With the option on (default), starting audio or video/RTSP playback prevents the machine's
   idle sleep, and the machine sleeps normally again shortly after playback stops — verified by
   run-and-observe evidence, not merely a build.
2. With the option off, playback does not affect the idle-sleep timer.
3. Video/RTSP playback also prevents the display from turning off; audio-only playback does not
   (per Decision 3).
4. The wake lock is released on stop, pause, session end, window close, and app exit; no
   orphaned wake state remains after the app closes.
5. The option persists across restart and defaults to on for pre-existing state.
6. Settings shows the option with English and Russian strings.
7. Build and tests pass; a run-and-observe check records `expected: ... | actual: ...` for the
   on state, the off state, and release-on-stop.

## Risks

- A missed release path would leave the machine unable to sleep after playback ends; the
  release must cover every exit, including failures.
- Idle-sleep suppression is slow to observe; verification needs a shortened idle-sleep timeout
  to confirm the effect within a single session.
