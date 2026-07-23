# SP-0014: ICY/Shoutcast now-playing metadata

**Status:** Implemented — BlockNeedUserTest (visual GUI observation of the
now-playing track text with a compatible/non-compatible stream). Exit condition:
user confirms the run-and-observe steps in
[SP-0014_icy_now_playing/06_verify.md](SP-0014_icy_now_playing/06_verify.md).

Tactical plan: [SP-0014_icy_now_playing/INDEX.md](SP-0014_icy_now_playing/INDEX.md)

## Implementation summary

- Core (platform-neutral): `IcyMetadataParser` (pure, sanitized, ≤512 chars) and
  `IcyMetadataReader` (dedicated `Icy-MetaData: 1` connection, injected
  `HttpClient`, never throws, reports changed titles via `IProgress<string?>`).
- App: `MainWindow.NowPlaying.cs` starts the reader on HTTP(S) audio playback,
  tears it down in `StopAudioPlayback()` (covers stop / switch / terminal-failure
  / window-hide), and a generation guard drops stale reports. Track folds into the
  existing now-playing line via the new localized `NowPlayingWithTrack` key
  (en + ru); station-only presentation is unchanged when no metadata arrives.
- Tests: 12 ICY tests (parser + loopback reader protocol). Full suite 68/68 green
  in Release; solution builds with 0 warnings. App smoke-launched against a live
  Icecast stream with no regression.
- No change to catalog refresh, the MANUAL/IMPORTED merge contract, `CatalogState`,
  or persistence; nothing is logged per-title or sent externally.

## Goal

Show the current artist and track supplied by an audio stream so the listener can understand what is on air without leaving StreamsPlayer.

## Why

The catalog identifies a station but not its changing broadcast content. ICY/Shoutcast metadata closes that gap and is a standard expectation in dedicated internet-radio players.

## Non-goals

- Identify tracks through an external service, audio fingerprinting, or catalog monitoring.
- Persist listening history; that is covered by SP-0019.
- Add metadata to video or RTSP streams.
- Add telemetry, accounts, or remote metadata storage.

## Constraints

- Metadata is requested only as part of an explicit HTTP(S) audio playback attempt.
- The current station title remains visible when the stream supplies no usable metadata.
- Metadata is session-only, bounded in size, treated as untrusted text, and cleared when playback stops, fails, or changes station.
- A malformed, unsupported, or absent metadata block must not interrupt otherwise playable audio.
- The complete user-facing flow is available in English and Russian.

## Acceptance criteria

1. A compatible ICY/Shoutcast audio stream can display its current artist/track beside the station identity while playing.
2. A stream without metadata plays with the existing station-only presentation and no metadata error.
3. Switching streams, stopping audio, or encountering a terminal playback failure removes stale now-playing text.
4. Oversized, malformed, empty, or rapidly changing metadata cannot crash the app, block playback, or leave text from a previous stream.
5. No now-playing content is sent externally or persisted after the session.
6. Automated parsing/state checks and a run-and-observe check with compatible and non-compatible streams pass.

## Risks

ICY servers vary in encoding and formatting, and some embed advertising or station text rather than artist/title fields. The UI must present a safe best-effort value without claiming normalized track identity.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md) and the ICY requirements in [streams specification](../docs/specifications/streams.txt).
