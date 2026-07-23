# SP-0019 Listening history — tactical plan

Strategic spec: [../SP-0019_listening_history.md](../SP-0019_listening_history.md) (Approved)

## Approach

A history entry is a bounded, recency-ordered record keyed by **channel identity**
(`StreamChannel.Id`). It is created only where playback already reaches its single
successful-play sink, promoted on replay, capped at 100, and surfaced in a dedicated
Recently-played window (deleted channels remain as non-playable labels). Retention,
dedup, and promotion are a pure Core function so they are unit-tested without WPF.

## Anchor evidence (working tree)

- Single success/fail sink for **all** media kinds: `RecordPlayOutcome(Guid, bool)`
  — [MainWindow.xaml.cs:855](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L855),
  called `true` from audio `AudioPlayer_MediaOpened`
  ([:676](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L676)) and from video
  `PlayerWindow` (`_recordOutcome(_channel.Id, true)`,
  [PlayerWindow.xaml.cs:392](../../src/StreamsPlayer.App/PlayerWindow.xaml.cs#L392)).
  Grid preview/probe paths never call it (SP-0015), so AC2 holds by construction.
- Latest ICY text arrives at `OnNowPlayingTitle(generation, title)`
  — [MainWindow.NowPlaying.cs:51](../../src/StreamsPlayer.App/MainWindow.NowPlaying.cs#L51).
- Persistence: `CatalogState` record + `StreamCatalogStore` atomic temp+move to
  `catalog-state.json`; new keys deserialize to initializer defaults on older files
  (no `SchemaVersion` bump — matches `HiddenCatalogUrls`, `KeepAwakeDuringPlayback`).
- Play entry point: `PlayChannelAsync(StreamChannel, rememberSelection)`.
- UI precedent: `HiddenChannelsWindow` (`.xaml`/`.xaml.cs`) + toolbar `*GlyphButton`
  style ([App.xaml](../../src/StreamsPlayer.App/App.xaml)) + `MainWindow.Hide.cs`
  open pattern; en/ru dictionaries with strict key parity.

## Design decisions

- **Store in `CatalogState`** (new `List<ListeningHistoryEntry> ListeningHistory`), not a
  separate file — follows every existing preference/list and reuses the atomic save.
- **No raw URL is stored.** An entry keeps `ChannelId`, `Title`, `MediaKind`,
  `LastPlayedAt`, `LastTrackText?`. Playback resolves `ChannelId` against the live
  catalog only; a missing id fails soft. This satisfies "do not reopen a deleted
  channel from a stale raw URL" by construction.
- **Entry point always visible** (discoverability) with a clock glyph; empty history
  shows an empty-state message.
- Actions are **Play** and **Clear history** only (spec requires no per-entry delete).

## Dependency-ordered phases

1. [PHASE-1_core_model_and_logic.md](PHASE-1_core_model_and_logic.md) — Core record,
   `CatalogState` list, pure `ListeningHistory` promotion/retention/track-update + tests.
   Produces: `ListeningHistoryEntry`, `CatalogState.ListeningHistory`, `ListeningHistory`.
2. [PHASE-2_app_capture_wiring.md](PHASE-2_app_capture_wiring.md) — record on success in
   `RecordPlayOutcome`; update track text in `OnNowPlayingTitle`.
   Consumes Phase 1. Produces: persisted history writes.
3. [PHASE-3_recently_played_window.md](PHASE-3_recently_played_window.md) —
   `ListeningHistoryWindow`, toolbar entry, Play (resolve-by-id) + Clear all.
   Consumes Phases 1–2.
4. [PHASE-4_localization_docs_validation.md](PHASE-4_localization_docs_validation.md) —
   en/ru parity, README, full build/tests, GUI run-and-observe; set ticket status.
   Consumes Phase 3.

## Criterion → phase coverage

| AC / constraint | Phase |
|---|---|
| AC1 successful play → top with local timestamp | 2 (capture), 3 (surface) |
| AC2 previews/probes/failures never create/promote | 2 (only `succeeded` branch) |
| AC3 replay updates existing entry; ≤100 retained | 1 (pure logic + tests) |
| AC4 latest ICY text shown, not verified identity | 1 (`UpdateTrackText`), 2, 3 |
| AC5 clear removes only history | 1/3 (empty list), 4 (observe) |
| AC6 restart/retention/deleted/privacy/localized checks | 1 (tests), 4 (GUI) |
| Deleted channel = non-playable label, fail soft; no stale-URL reopen | 3 (resolve by id) |
| Core stays platform-neutral | 1 |
