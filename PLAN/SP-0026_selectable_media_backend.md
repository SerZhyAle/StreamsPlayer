# SP-0026: Selectable alternative media backend (video/RTSP fallback)

**Status:** BlockNeedUserTest — code-complete (Phases 1–4 + Flyleaf), build + 149 tests green;
awaiting user GUI run-and-observe (LibVLC parity, engine switch, persistence) and, for FlyleafLib
runtime playback, deployment of the FFmpeg v8 x64 natives. Exit condition in the tactical INDEX.

Tactical plan: [SP-0026_selectable_media_backend/INDEX.md](SP-0026_selectable_media_backend/INDEX.md)

## Goal

Give the video/RTSP live player a second, user-selectable playback engine as a **troubleshooting
fallback**, surfaced as an option in Settings. The current engine (LibVLC) stays the default and
proven baseline; the alternative (FlyleafLib) is an engine the user can flip to when a specific
stream misbehaves under the default. The alternative must cover the same stream families — HLS
live, RTSP, poor-quality third-party live sources — while preserving today's playback resilience.

## Why

Every live video/RTSP stream is played through one engine. When a specific stream behaves badly
on that engine (a decode, timestamp, or protocol quirk), the user has no recourse — the app can
only fail that stream. A selectable second engine gives the user (and support) a real fallback: a
stream that stalls or refuses on LibVLC can be retried on FlyleafLib without leaving the app.
Research (SP-0026 dossier) identifies FlyleafLib as a viable, actively-maintained,
license-compatible alternative, making the choice practical rather than theoretical.

## Non-goals

- No change to the audio-only playback path; it stays on the current WPF `MediaElement` engine.
- No change to the headless grid-thumbnail grabber; it stays on the current engine.
- No removal of the current engine; LibVLC stays the default and the baseline.
- No change to `StreamsPlayer.Core`: it stays platform-neutral with no media dependency.
- No change to the catalog refresh contract or the MANUAL/IMPORTED merge protection.
- No automatic per-stream engine switching in this ticket; the choice is user-driven. (A
  per-stream "retry on the other engine" action is a possible follow-up, out of scope here.)
- No new logging facade in App/Core beyond the existing one.

## Decisions

1. **Scope — video/RTSP player only.** The engine choice applies solely to the live video/RTSP
   player window. Audio and thumbnail capture are unchanged. This is the smallest seam that
   captures the whole payoff, since that window is where the resilience tuning lives.
2. **Intent — troubleshooting fallback.** LibVLC remains default and pre-selected everywhere.
   FlyleafLib is presented as the engine to switch to when a stream misbehaves; Settings copy
   frames it as a fallback/experimental alternative, not a co-equal default.
3. **Candidate — FlyleafLib (locked in).** LGPL-3.0 (license-compatible with the MSIX/winget/
   self-contained model, matching LibVLC's posture), actively maintained (v3.10.4, 2026-05-23,
   .NET 10 / FFmpeg 8 build), with HLS-live + RTSP + audio support and a first-class WPF control.
4. **Packaging — ship both native stacks.** Because the fallback must be available at runtime
   alongside the default, both the LibVLC and the FlyleafLib/FFmpeg native stacks ship in the
   package. The installer/EXE size grows accordingly; this is accepted for the fallback value.
5. **Parity bar — experimental first, parity is the goal.** The alternative may ship behind an
   explicit "experimental" label before it reaches full behaviour parity, provided it plays HLS
   live and RTSP. Full parity with the resilience baseline is the target, not a launch gate.

## Constraints

- LibVLC remains the default; a fresh install and any existing state behave exactly as today
  until the user changes the option.
- FlyleafLib must, at minimum, play HLS-live and RTSP streams; the target is to preserve the
  shipped resilience behaviour for bad live streams — software-decode tolerance, clock-jitter
  tolerance, forced RTSP-over-TCP, a single sane live buffer with rebuffer-in-place (never
  reconnect to grow the buffer), per-stream audio/subtitle track selection where offered,
  thumbnail capture on first live frame, and freeze/stream-drop recovery. Parity is measured
  against `docs/stream-playback-recommendations.md`; gaps are surfaced as the "experimental"
  label (Decision 5), not silently accepted.
- The setting persists across restart alongside existing preferences and defaults to LibVLC.
- Any new user-facing text is localized in English and Russian; no emoji.
- The engine seam lives in `StreamsPlayer.App`; Core stays untouched and media-free. The
  dependency direction (App → Core) is unchanged.

## Acceptance criteria

1. Settings presents an engine choice for the video/RTSP player; LibVLC is pre-selected on a
   fresh install and for any pre-existing state, and is labelled the default.
2. Selecting FlyleafLib and opening a video/RTSP stream plays it through FlyleafLib, verified by
   run-and-observe evidence (not merely a build), including a stream known to be troublesome on
   LibVLC.
3. Switching back to LibVLC restores today's exact behaviour.
4. FlyleafLib plays at least one HLS-live and one RTSP stream; resilience behaviours it does or
   does not yet reproduce are evidenced from the session log / observation, and any shortfall is
   reflected by the experimental label rather than a crash or silent freeze.
5. The choice persists across restart.
6. Core has no media dependency; App → Core direction unchanged. Audio and thumbnail paths are
   unchanged.
7. New Settings strings are present in EN and RU.
8. Build and tests pass; a run-and-observe check records `expected: … | actual: …` for the
   switch, playback on each engine, and persistence.

## Risks

- **Behaviour parity is the hard part.** LibVLC carries a large, hard-won option set for bad live
  streams. A naive FlyleafLib integration can regress into the multi-second freezes that tuning
  already eliminated. The experimental label (Decision 5) contains — but does not remove — this
  risk; parity work must be evidenced, not assumed.
- **Payload growth.** Shipping both native stacks (Decision 4) enlarges the installer/EXE; watch
  the MSIX/self-contained size and confirm it stays acceptable.
- **Decoupling churn.** The player is tightly bound to LibVLC types; introducing the backend seam
  touches the whole player window and must not regress fullscreen, volume/mute, track menus,
  thumbnails, or the failure dialog.
- **Two code paths to maintain.** Future playback fixes may need to land in both engines.

## References

- Research dossier: `PLAN/SP-0026_selectable_media_backend/research.md`
- Playback tuning baseline: `docs/stream-playback-recommendations.md`
