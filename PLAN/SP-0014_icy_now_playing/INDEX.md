# SP-0014 Tactical Plan — ICY/Shoutcast now-playing metadata

Strategic ticket: [SP-0014](../SP-0014_icy_now_playing.md) (Approved)

## Design decisions (from research)

- **Separate metadata connection.** Audio plays through a WPF `MediaElement`
  ([MainWindow.xaml.cs:620-621](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L620-L621)),
  which exposes no ICY API. The only viable path — and the one the product spec
  mandates (`streams.txt` Part D: send `Icy-MetaData: 1`) — is a second, best-effort
  HTTP(S) connection to the same URL that reads only the interleaved metadata.
  This costs one extra low-bitrate radio connection while audio plays; it is torn
  down on every stop/switch/failure.
- **Core owns parsing; App owns the connection wiring and UI.** Pure
  `StreamTitle` extraction (untrusted, size-bounded, control-stripped) lives in
  `StreamsPlayer.Core` with unit tests. The HTTP streaming reader also lives in
  Core but takes an injected `HttpClient` (mirrors `StreamCatalogService`), so
  Core stays platform-neutral (no WPF). `MediaElement`, dispatcher marshaling,
  and `NowPlayingText` stay in App.
- **UI: fold track into the existing now-playing line.** The spec requires the
  station identity to stay visible when there is no metadata ("beside the station
  identity"). Reuse the existing `NowPlayingText`
  ([MainWindow.xaml:369](../../src/StreamsPlayer.App/MainWindow.xaml#L369)) and
  `SetNowPlaying(key, args)` re-localization machinery
  ([MainWindow.Localization.cs:115-126](../../src/StreamsPlayer.App/MainWindow.Localization.cs#L115-L126))
  via a new format key `NowPlayingWithTrack` = `"Now playing: {0} — {1}"`. No new
  XAML element; station-only presentation is the existing `NowPlaying` key.
- **Stale-text guard.** A monotonically increasing generation counter, captured
  per playback start and compared inside the marshaled callback, drops any report
  from a superseded reader (AC #3/#4). Teardown is centralized in
  `StopAudioPlayback()` ([MainWindow.xaml.cs:708-717](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L708-L717)),
  which already covers stop, station-switch, terminal failure (via `StopAudio`),
  and window-hide.

## Phases (dependency order)

| # | Phase | Produces | Consumes |
|---|-------|----------|----------|
| 01 | [Core ICY parser](01_core_parser.md) | `IcyMetadataParser` | — |
| 02 | [Core ICY reader](02_core_reader.md) | `IcyMetadataReader` | 01 |
| 03 | [Core unit tests](03_core_tests.md) | `IcyMetadataParserTests` | 01 |
| 04 | [Localized strings](04_localization.md) | `NowPlayingWithTrack` (en/ru) | — |
| 05 | [App lifecycle + UI wiring](05_app_wiring.md) | `MainWindow.NowPlaying.cs` | 02, 04 |
| 06 | [Verification](06_verify.md) | build/test/run evidence | 01–05 |

## Criterion → phase coverage

| Acceptance criterion | Phase(s) |
|----------------------|----------|
| 1. Compatible stream shows artist/track beside station | 02, 04, 05 |
| 2. Stream without metadata plays station-only, no error | 02 (no `icy-metaint` → graceful), 05 |
| 3. Switch/stop/terminal-failure removes stale text | 05 (StopAudioPlayback + generation guard) |
| 4. Oversized/malformed/empty/rapid metadata is safe | 01 (bound/sanitize), 02 (never throws), 05 (generation guard) |
| 5. Nothing sent externally or persisted after session | 02 (in-memory only), 05 (no CatalogState write) |
| 6. Automated parse/state checks + run-and-observe | 03, 06 |

| Constraint | Phase(s) |
|------------|----------|
| Requested only during explicit HTTP(S) audio playback | 05 (audio branch + http/https scheme guard) |
| Station title stays visible without metadata | 04, 05 |
| Session-only, bounded, untrusted, cleared on stop/fail/switch | 01, 02, 05 |
| Malformed/absent metadata never interrupts audio | 02 (best-effort, catch-all), 05 (fire-and-forget isolated) |
| Full flow in English and Russian | 04, 05 |

## Architecture guardrails

- `StreamsPlayer.Core` gains no WPF/App/tool reference (Phases 01–02).
- No change to `CatalogState`, `StreamCatalogStore`, `CatalogMerger`, or the
  MANUAL/IMPORTED merge contract; no automatic background download is added.
- No new logging facade; App keeps `CurrentLog`, Core stays log-free.
