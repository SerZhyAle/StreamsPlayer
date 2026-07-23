# Phase 06 — Verification

**Produces:** build/test/run evidence recorded on the ticket
**Consumes:** Phases 01–05

## Automated checks

1. `dotnet build StreamsPlayer.sln -c Release`
   expected: succeeds | actual: Build succeeded, 0 Warning(s), 0 Error(s).
2. `dotnet test StreamsPlayer.sln -c Release --no-build`
   expected: all pass incl. `IcyMetadataParserTests` | actual: Passed 68/68
   (includes `IcyMetadataParserTests` 10 + `IcyMetadataReaderTests` 2).
   (equivalently `./scripts/check.ps1` for the release-parity gate.)

## Runtime smoke evidence (automated, non-visual)

- Launched the built app; it stayed alive 6 s and closed cleanly — no startup
  regression from the new `_icyHttpClient` field / lifecycle hooks.
  expected: process alive, no crash | actual: `alive_after_6s=True`, `closed_ok`.
- Session `Current.log` shows the restored-session audio channel opening against a
  live Icecast stream (`0nlineradio.radioho.st/...`): `AUDIO OPEN` → `AUDIO LIVE`,
  no exception. The ICY reader runs on this real HTTP path without disturbing
  playback.
  expected: clean AUDIO OPEN/LIVE, no error | actual: both logged, no error.

## Run-and-observe (AC #6, GUI)

Launch `./run.ps1` and, with the audio filter:

- **Compatible ICY stream** (an Icecast/Shoutcast MP3 radio URL that sends
  `icy-metaint`): play it, confirm the bottom line changes from
  `Now playing: <station>` to `Now playing: <station> — <artist - track>` once
  metadata arrives, and updates when the track changes.
  expected: track text appears beside station and refreshes | actual: needs user observation (BlockNeedUserTest)
- **Non-compatible stream** (audio with no ICY metadata): play it, confirm it
  plays with station-only text and no error/dialog.
  expected: `Now playing: <station>`, no error | actual: needs user observation (BlockNeedUserTest)
- **Teardown**: stop, then switch stations, then trigger a failure (bad URL):
  confirm the now-playing line clears to `Nothing playing` / the new station with
  no stale track text.
  expected: no stale track text after stop/switch/fail | actual: needs user observation (BlockNeedUserTest)
- **Language toggle mid-track**: switch EN↔RU while a track is showing.
  expected: line re-renders in the other language, track text preserved | actual: needs user observation (BlockNeedUserTest)

Record each `expected | actual`. If a compatible live ICY stream cannot be
exercised in this environment, set the ticket to `BlockNeedUserTest` with the
exact steps above rather than claiming Verified.
