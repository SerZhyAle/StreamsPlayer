# SP-0026 tactical plan — Selectable alternative media backend (video/RTSP)

**Status:** Code-complete across Phases 1–4 + Phase 3 (Flyleaf); build + 149 tests green (Release,
0 warnings). **BlockNeedUserTest** for the GUI run-and-observe matrix (Phase 5) — video playback
cannot be self-observed by the agent — and for FlyleafLib runtime playback, which additionally
requires the FFmpeg v8 x64 natives to be deployed (see below).

### Execution results (checks recorded)

- **Phase 1** — `MediaBackend` enum + `CatalogState.VideoBackend` (default `LibVlc`), 4 Core tests.
  `dotnet test` Core → expected pass | actual **149 passed** (was 145). Core media-free → OK.
- **Phase 2** — `IVideoBackend`/`VideoTrack`, `LibVlcVideoBackend` (verbatim LibVLC extraction),
  `PlayerWindow` refactored to the seam, XAML host swap, factory. `dotnet build -c Release` →
  expected succeeds, no LibVLC symbols in `PlayerWindow.xaml.cs` | actual **build OK, grep clean**.
  Video-window visual parity → **BlockNeedUserTest**.
- **Phase 4** (ran before 3) — engine ComboBox in Settings→Playback, persistence, `_state.VideoBackend`
  threaded into `PlayerWindow`, EN+RU strings. Build → OK. Localization key parity → **4 keys each**.
  Switch/persist observe → **BlockNeedUserTest**.
- **Phase 3** — `FlyleafLib` 3.10.4 + `Flyleaf.FFmpeg.Bindings` 8.0.1 (restore OK),
  `FlyleafVideoBackend` mapped to the verified API, factory selects Flyleaf with a **graceful
  LibVLC fallback** on init failure. Build → **OK, 0 warnings** (compiler validated every Flyleaf
  member used). Managed payload add-on **3.15 MB**; FFmpeg natives folder absent → fallback path
  active. Flyleaf runtime playback → **BlockNeedUserTest** (needs natives).

### Exit condition (to move past BlockNeedUserTest)

1. User runs the app on VLC (default): confirms video + RTSP playback, fullscreen, volume/mute,
   track menus, save-frame thumbnail, and recovery behave exactly as before the refactor.
2. User confirms the Settings engine toggle persists across restart and defaults to VLC.
3. To verify FlyleafLib itself: deploy the FFmpeg v8 **x64** DLLs into an `FFmpeg` folder beside
   `StreamsPlayer.exe` (from the Flyleaf v3.10.4 GitHub release), select FlyleafLib, and confirm one
   HLS-live + one RTSP stream play. Without the natives the app logs `FLYLEAF FALLBACK to=libvlc`
   and keeps playing on VLC (no crash) — which itself satisfies the "never a crash" constraint.

Strategic ticket: [../SP-0026_selectable_media_backend.md](../SP-0026_selectable_media_backend.md)
Research dossier: [research.md](research.md)
Playback tuning baseline: `docs/stream-playback-recommendations.md`

## Design summary

Today the video/RTSP `PlayerWindow` (795 lines) is bound directly to `LibVLCSharp` types
(`LibVLC`, `MediaPlayer`, `Media`, `VideoView`) across its whole surface: open-with-options,
volume/mute, snapshot, track menus, live stats, and the SP-0015 recovery policy / stall watchdog /
teardown threading. The user constraint is absolute: **LibVLC stays the exact default** — a fresh
install and any existing state behave byte-for-byte as today; FlyleafLib is opt-in only.

The seam is an **App-side interface `IVideoBackend`** (Core stays media-free). Two concerns are
separated inside `PlayerWindow`:

- **Engine-agnostic orchestration stays in `PlayerWindow`:** window chrome, fullscreen, volume
  slider, mute, topmost, the recovery-policy driver (`RecoverAsync`), the stall watchdog, the
  failure dialog, the frame-saved toast, and thumbnail hand-off. These already operate on abstract
  signals plus `_mediaPlayer.Time` / `.State`, so they only need to read those through the seam.
- **Engine-specific media ops move behind `IVideoBackend`:** a WPF view element to host, play
  (URL + cache/tcp/software-decode options + reconnect flag), stop/dispose (off-UI-thread teardown),
  volume/mute, snapshot→`BitmapSource`, track enumeration/selection, position/state, and the events
  the orchestration consumes (buffering %, playing/opening/stopped/end-reached, encountered-error,
  tracks-changed, snapshot-ready).

`LibVlcVideoBackend` is a **near-verbatim move** of the current code — the default path must not
change. `FlyleafVideoBackend` is the experimental second implementation. A one-line factory picks
the implementation from the persisted `MediaBackend`. Because both native stacks ship (Decision 4),
no build-time gating; the choice is pure runtime.

Non-goals held: audio (`MediaElement`) and headless grid-thumbnail capture
(`VideoFrameCaptureService`, still LibVLC) are untouched; Core gains only an enum + one persisted
field and keeps zero media dependency; catalog refresh and MANUAL/IMPORTED merge are not involved.

## Phases (dependency-ordered)

| Phase | Produces | Consumes |
| --- | --- | --- |
| [PHASE-1](PHASE-1_core_backend_setting.md) — Core persisted setting | `MediaBackend` enum, `CatalogState.VideoBackend` (default `LibVlc`) + Core tests | — |
| [PHASE-2](PHASE-2_app_backend_seam.md) — App seam + LibVLC extraction | `IVideoBackend`, backend signal/track/event types, `LibVlcVideoBackend`, `PlayerWindow` refactored to the seam, backend factory (LibVLC only) | Phase 1 |
| [PHASE-3](PHASE-3_flyleaf_backend.md) — Flyleaf implementation | `FlyleafLib` package, `FlyleafVideoBackend`, factory selects Flyleaf when persisted | Phase 2 |
| [PHASE-4](PHASE-4_settings_wiring.md) — Settings UI + wiring + strings | engine ComboBox in Settings, persist `VideoBackend`, pass into `PlayerWindow`, EN+RU strings | Phase 1, Phase 3 |
| [PHASE-5](PHASE-5_validation.md) — Validation + docs | build/test evidence, run-and-observe matrix, tuning/README notes | Phases 1–4 |

## Criterion / constraint coverage

| Spec item | Phase(s) |
| --- | --- |
| AC 1 Settings engine choice; LibVLC pre-selected + labelled default (fresh + pre-existing state) | 1, 4 |
| AC 2 Selecting Flyleaf plays via Flyleaf (run-and-observe, incl. LibVLC-troublesome stream) | 3, 4, 5 |
| AC 3 Switching back to LibVLC restores today's exact behaviour | 2, 5 |
| AC 4 Flyleaf plays ≥1 HLS-live + ≥1 RTSP; resilience gaps evidenced, no crash/silent freeze; experimental label | 3, 4, 5 |
| AC 5 Choice persists across restart | 1, 4, 5 |
| AC 6 Core has no media dependency; App→Core unchanged; audio + thumbnail paths unchanged | 1, 2, 3 |
| AC 7 New Settings strings in EN + RU | 4 |
| AC 8 Build + tests pass; run-and-observe `expected/actual` for switch, each engine, persistence | 5 |
| Constraint: LibVLC default; identical until user changes option | 1, 2, 4 |
| Constraint: Flyleaf min HLS-live + RTSP; parity target measured vs tuning doc; gaps → experimental label not silent | 3, 5 |
| Constraint: setting persists alongside existing preferences, defaults LibVLC | 1 |
| Constraint: new text localized EN+RU, no emoji | 4 |
| Constraint: seam in App; Core untouched/media-free; App→Core direction unchanged | 1, 2 |
| Risk: no regression to fullscreen/volume/mute/tracks/thumbnails/failure dialog | 2, 5 |
| Risk: payload growth from both native stacks — confirm acceptable | 5 |
