# SP-0027 Tactical Plan — Keep the computer awake during active playback

Strategic spec: [../SP-0027_keep_awake_during_playback.md](../SP-0027_keep_awake_during_playback.md)

## Design

A new App-only helper, `WakeGuard`, wraps the Win32 `SetThreadExecutionState` API with
reference-counted **system** and **display** requests, gated by an `Enabled` flag that mirrors
the persisted preference. All calls run on the single WPF UI thread.

- **Audio session** holds a system-only request (Decision 3: radio does not need the screen lit).
  Acquired when a session starts in `PlayChannelAsync`; released in `StopAudioPlayback`. The hold
  spans bounded reconnects because the session (`_playingAudio`) stays alive across them.
- **Video/RTSP session** holds system + display for the `PlayerWindow` session lifetime
  (`Loaded` → `Closed`). Tying to the window avoids marshaling LibVLC's thread-affine, flapping
  `Playing/Paused/Stopped/EndReached` events and guarantees release on close and app exit.
- **Enabled** is set from `CatalogState.KeepAwakeDuringPlayback` on load and on Settings save;
  toggling recomputes the applied execution state immediately, so an active lock is released the
  moment the option is turned off and re-acquired when turned on while playing.
- `App.OnExit` calls `WakeGuard.Reset()` as a final safety net so no wake state outlives the app.

Ref-counting is required because audio and one-or-more `PlayerWindow`s can be active at once; a
naive single release from one session would prematurely let the machine sleep while another plays.

## Phases (dependency order)

1. **Phase 1 — Core preference.** Add `KeepAwakeDuringPlayback` (default `true`) to `CatalogState`.
   Static check: `dotnet build src/StreamsPlayer.Core` succeeds; grep shows the property with `= true`.
2. **Phase 2 — WakeGuard helper.** New `src/StreamsPlayer.App/WakeGuard.cs`: P/Invoke +
   ref-counted system/display counters + `Enabled` + `Reset`. Static check: `dotnet build` of the App.
3. **Phase 3 — Wire playback sessions.** Acquire/release for audio (`MainWindow.xaml.cs`) and video
   (`PlayerWindow.xaml.cs`). Static check: build; grep confirms acquire+release pairs.
4. **Phase 4 — Settings UI + persistence + localization.** Checkbox in `SettingsWindow`,
   pass/persist/apply in `MainWindow.Settings.cs`, set `Enabled` on load, two localized keys per
   language, `App.OnExit` reset. Static check: `./build.ps1 -Test` (Debug) green.
5. **Phase 5 — Verify.** Run-and-observe with a shortened idle-sleep timeout: on-state prevents
   sleep, off-state does not, release-on-stop lets it sleep. Record `expected | actual`.

## Non-goals guardrails (from the spec)
- No Core OS dependency (Core change is a plain bool only).
- Grid preview capture must never acquire a wake lock.
- Only the idle-sleep timer is suppressed (`ES_CONTINUOUS | ES_SYSTEM_REQUIRED [| ES_DISPLAY_REQUIRED]`).
