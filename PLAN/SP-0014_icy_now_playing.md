# SP-0014: ICY/Shoutcast now-playing metadata

**Status:** Approved

## Goal

Show the current artist and track supplied by an audio stream so the listener can understand what is on air without leaving StreamPlayer.

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
