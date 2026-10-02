---
name: project_playback
description: Playback engines (LibVLC, FlyleafLib, MediaElement), FFmpeg licensing, ICY metadata, and recovery state machines
type: project
---

# Playback Engines & Stream Decoding

- **FFmpeg licensing & FlyleafLib binaries (SP-0026, SP-0128):**
  - Upstream FlyleafLib FFmpeg binaries are GPLv3 and cannot be bundled.
  - StreamsPlayer downloads an LGPL build (`BtbN/FFmpeg-Builds`, `win64-lgpl-shared-8.1`) on explicit user request into `%LOCALAPPDATA%\StreamsPlayer\FFmpeg`.
  - `Flyleaf.FFmpeg.Bindings` is pinned to 8.0.1 against FlyleafLib's 7.1.1 dependency (upstream-documented requirement).
- **ICY metadata & Shoutcast v1 streams (SP-0073, SP-0074):**
  - LibVLC surfaces ICY now-playing over HTTP, but not HTTPS. `MetadataType.Title` is the URL tail; only consult `NowPlaying`.
  - FlyleafLib demuxer reads metadata only once at stream open; live ICY updates require reading `icy_metadata_packet` directly from FFmpeg format context (`AllowUnsafeBlocks` in `FlyleafVideoBackend.ReadNowPlaying`).
  - Shoutcast v1 responds with `ICY 200 OK` which throws in .NET `HttpClient` (`HttpRequestError.InvalidResponse`); resolved via raw socket fallback in `IcyMetadataReader`.
- **Live playback recovery policy (SP-0015):**
  - Managed by Core state machine (`LivePlaybackRecoveryPolicy`).
  - Retry budgets are consecutive and reset upon sustained live playback.
  - Reconnects trigger on silent freezes (frozen position ~9s while nominally playing) or buffering > 15s without position progress.
- **Adaptive stream capping & rendition detection (SP-0071, SP-0076, SP-0077):**
  - LibVLC 3 requires setting both `:adaptive-maxwidth` and `:adaptive-maxheight` at media open; changing resolution cap requires re-opening media.
  - On adaptive streams, `MediaPlayer.VideoTrack` stays -1 and `MediaPlayer.Size` returns initial dimensions; read highest video ES track ID in `Media.Tracks` to determine active rendition.
  - For throughput measurement on HLS/DASH, measure delta of `demux_bytes` (access module `read_bytes` and `in_bitrate` remain frozen).
- **Flyleaf live latency control (SP-0078):**
  - Setting `Player.Speed` directly forces rebuffering and stalls. Use `Config.Player.MaxLatency` for non-stuttering pacing corrections.
- **What LibVLC 3.0.23 reports when a radio open fails (SP-0189, measured 2026-10-02):**
  - `--quiet` does not silence the `LibVLC.Log` callback, and `LibVLC.LastLibVLCError` stays empty for playback failures - the cause is only in the Error-level log lines, written *before* `EncounteredError` fires.
  - `EncounteredError` comes only from an input that could not be opened (refused, DNS, HTTP 4xx/5xx, TLS, reset before headers). A mid-stream drop - even a TCP reset - is `EndReached`; HTML or random bytes served as audio "play" and end; an audio output that cannot start raises no event at all (only `AUDIO SILENT` catches it).
  - Why it matters: an engine error event is never evidence of a local device fault. How to apply: never classify a radio failure by a wrapper's type name; read the trail for the log, decide by the event and the app's own probes.
- **The smoke's audio round depends on the machine having a default audio output (2026-10-02):** with none, LibVLC logs `mmdevice: cannot get default device (error 0x80070490)` and `AUDIO SILENT` shows `decoded_blocks>0, played_buffers=0` for *any* build. Before blaming a change, run the same round against a pre-change binary (`smoke-playback.ps1 -AppPath ..`).
