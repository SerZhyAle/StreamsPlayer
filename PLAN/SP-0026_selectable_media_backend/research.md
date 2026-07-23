# SP-0026 Research — Alternative media backend, selectable in Settings

**Type:** read-only investigation (streamsplayer-research)
**Question:** Discover a viable alternative to LibVLC and expose it as a selectable option in Settings.

## 1. Current backend wiring (working tree is authority)

StreamsPlayer already runs **two** media backends, split by media kind — there is no single
"VLC player" to swap:

| Path | Backend | Evidence |
|---|---|---|
| Audio (Icecast/Shoutcast) | WPF `MediaElement` (`AudioPlayer`) via MediaFoundation | [MainWindow.xaml.cs:608-624](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L608-L624) |
| Video / RTSP live player | LibVLC (`LibVLCSharp.WPF`) | [PlayerWindow.xaml.cs:74-96](../../src/StreamsPlayer.App/PlayerWindow.xaml.cs#L74-L96) |
| Headless grid-thumbnail capture | LibVLC (off-screen, software decode) | [VideoFrameCaptureService.cs:18-55](../../src/StreamsPlayer.App/VideoFrameCaptureService.cs#L18-L55) |

- LibVLC coupling is confined to `StreamsPlayer.App`; `StreamsPlayer.Core` has **no** media
  dependency and must stay platform-neutral. A backend swap touches App only.
- Packages today: `LibVLCSharp` 3.10.0, `LibVLCSharp.WPF` 3.10.0, `VideoLAN.LibVLC.Windows`
  3.0.23.1 ([StreamsPlayer.App.csproj:8-10](../../src/StreamsPlayer.App/StreamsPlayer.App.csproj#L8-L10)).
- The player is **not** a thin wrapper. It depends on a large, hard-won LibVLC option/behaviour
  surface documented in [docs/stream-playback-recommendations.md](../../docs/stream-playback-recommendations.md):
  forced software decode (`--avcodec-hw=none`), `--clock-jitter=0`, `--rtsp-tcp`, 15 s
  `network-caching`/`live-caching`, rebuffer-in-place (never reconnect to grow buffer),
  per-media snapshot for thumbnails, per-stream audio/subtitle track selection, live-stats
  counters, and a freeze/EndReached reconnect watchdog. Any alternative must reproduce the
  *behaviour*, not just "play a URL".

## 2. Settings persistence pattern (where an option would live)

- Settings state is a Core record `CatalogState`
  ([Models.cs:122-152](../../src/StreamsPlayer.Core/Models.cs#L122-L152)); a new option is one
  more init-property with a default (e.g. the existing `TileSize`, `UpdateStreamPreviews`).
- The Settings dialog reads/writes via constructor args + public getters
  ([SettingsWindow.xaml.cs:15-39](../../src/StreamsPlayer.App/SettingsWindow.xaml.cs#L15-L39)),
  persisted through `_store.SaveAsync(_state with { ... })`
  ([MainWindow.Settings.cs:30-34](../../src/StreamsPlayer.App/MainWindow.Settings.cs#L30-L34)).
- New user-facing strings are localized EN + RU (`Localization.en/ru.xaml`); no emoji.
- So "selectable backend option" is a well-trodden mechanical pattern **once** the player is
  abstracted behind a backend seam. The hard part is the seam, not the checkbox.

## 3. Candidate alternatives (verified against current sources, July 2026)

| Candidate | License | Maintenance | HLS live | RTSP | Audio-only | WPF host | Native footprint | Verdict |
|---|---|---|---|---|---|---|---|---|
| **FlyleafLib** (FFmpeg/DirectX) | **LGPL-3.0** | **v3.10.4, 2026-05-23; .NET 10 + FFmpeg 8 build** | Yes (HLS live seeking) | Yes | Yes (audio without UI control) | `FlyleafME` WPF control + D3D host | FFmpeg libs (per-arch, chosen from FFmpeg releases) | **Strongest** |
| libmpv via Mpv.NET / Mpv.WPF | **GPLv2** (viral) | Binding forks scattered; core mpv active | Yes | Yes | Yes | `Mpv.WPF` user control (code-instantiated) | libmpv `mpv-1.dll` ~40 MB+ | Capable but **GPLv2 is a redistribution problem** |
| Unosquare **FFME** | Ms-PL/derivative | Uncertain (no clear 2025-26 release signal) | Yes | Not clearly supported | Yes (MediaElement drop-in) | Drop-in `MediaElement` replacement | FFmpeg shared libs | Weak — RTSP gap + maintenance risk |
| WPF `MediaElement` (already shipped) | n/a (in-box) | in-box | No (unreliable) | No | Yes | native | none | Already the audio backend; not a video option |
| Windows.Media.Playback (MediaFoundation) | in-box | in-box | Yes (AdaptiveMediaSource) | Weak/none | Yes | needs D3DImage/airspace interop | none | RTSP gap; heavy WPF interop |

**Recommendation: FlyleafLib.** It is the only candidate that (a) matches LibVLC's protocol
coverage (HLS live + RTSP + audio), (b) shares LibVLC's **LGPL** redistribution posture — safe
for the MSIX/winget distribution model, unlike mpv's GPLv2 — (c) ships a current **.NET 10 /
FFmpeg 8** build actively maintained in 2026, and (d) offers a first-class WPF control and a
hardware-accelerated D3D surface with software-decode fallback.

## 4. Architectural implication (the real design decision)

To make the backend **selectable**, `PlayerWindow` (and optionally the preview capture) must be
decoupled from `LibVLCSharp` behind an App-side backend abstraction (open/play, volume/mute,
track selection, snapshot, buffering/stall/error events, dispose). Today those types are used
directly throughout `PlayerWindow`. This is a genuine refactor, not a config toggle — it is the
justification for a strategic spec rather than a `quick`/`fix`.

Adding FlyleafLib also grows the shipped payload (a second FFmpeg native stack alongside libVLC)
unless one backend is chosen at build/runtime — a packaging/size decision for the spec.

## 5. Open questions (carried into the strategic spec)

1. **Scope** — does the option cover only the **video/RTSP player** (`PlayerWindow`), or also
   audio and the headless thumbnail grabber? (Recommend: video/RTSP player first.)
2. **Intent** — is the alternative a **troubleshooting fallback** the user flips when a stream
   misbehaves under LibVLC, or a permanent co-equal backend? This changes default + copy.
3. **Packaging** — ship both native stacks (larger installer) or gate one? Affects MSIX size.
4. **Behaviour parity bar** — must the alternative reproduce the full tuning set (§1) before it
   is offered, or is a "best-effort experimental" label acceptable at first?

## Sources

- [SuRGeoNix/Flyleaf (GitHub)](https://github.com/SuRGeoNix/Flyleaf) — LGPL-3.0; v3.10.4 (2026-05-23), .NET 10 / FFmpeg 8; HLS live, RTSP, audio, WPF `FlyleafME` control, D3D host.
- [FlyleafLib on NuGet](https://www.nuget.org/packages/FlyleafLib/) — package availability/versions.
- [unosquare/ffmediaelement (GitHub)](https://github.com/unosquare/ffmediaelement) — FFME, WPF MediaElement replacement, HLS; RTSP/maintenance unconfirmed.
- [hudec117/Mpv.NET-lib- (GitHub)](https://github.com/hudec117/Mpv.NET-lib-) / [Mpv.WPF on NuGet](https://libraries.io/nuget/Mpv.WPF) — libmpv binding; GPLv2 obligation noted.
