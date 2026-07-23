# PHASE-2 — App capture wiring

Produces: persisted history writes on successful play and on ICY track changes.
Consumes: Phase 1 (`ListeningHistory`, `CatalogState.ListeningHistory`).

## Steps

1. **Record on success** — in `RecordPlayOutcome(Guid id, bool succeeded)`
   ([MainWindow.xaml.cs:855](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L855)), when
   `succeeded`, fold a history update into the same state mutation that already runs
   before `SaveAsync`, e.g.:

   ```csharp
   var next = _state with { /* existing channel replacement stays via ReplaceChannel */ };
   if (succeeded)
   {
       next = next with
       {
           ListeningHistory = ListeningHistory.RecordPlay(
               _state.ListeningHistory, channel.Id, channel.Title, channel.MediaKind, DateTimeOffset.UtcNow)
       };
   }
   ```
   Keep the existing `ReplaceChannel(...)` for the channel's `LastPlayed*` fields, then
   apply the history `with` and a single `_state = await _store.SaveAsync(...)`. The
   failure branch (`succeeded == false`) must **not** touch `ListeningHistory` (AC2).
   Reuse the same `DateTimeOffset.UtcNow` already captured for the outcome so the
   channel mark and history timestamp agree.

2. **Update track text on ICY** — in `OnNowPlayingTitle(int generation, string? title)`
   ([MainWindow.NowPlaying.cs:51](../../src/StreamsPlayer.App/MainWindow.NowPlaying.cs#L51)),
   after the existing generation/`_playingAudio` guards and the `SetNowPlaying(...)` call,
   fire-and-forget a persist for a **non-empty** title:
   `_ = PersistNowPlayingHistoryAsync(_playingAudio.Channel.Id, title);`
   Add `private async Task PersistNowPlayingHistoryAsync(Guid id, string? title)` that:
   - computes `var next = ListeningHistory.UpdateTrackText(_state.ListeningHistory, id, title);`
   - returns early if `ReferenceEquals(next, _state.ListeningHistory)` **or** the entry
     text is unchanged (Phase-1 `UpdateTrackText` already returns an unchanged-content
     list on no-op; guard on a cheap equality to skip the disk write, mirroring the
     `if (_state.AudioVolume == volume) return;` pattern);
   - otherwise `_state = await _store.SaveAsync(_state with { ListeningHistory = next });`
   Only whitespace/empty titles are ignored here (they never overwrite a good line);
   this matches SP-0014, which folds empty ICY back to station-only display.

3. **No new logging or Core dependency** — App-side only; no `Console.WriteLine`, no new
   facade. The ICY reader, recovery, and preview paths are untouched.

## Static verification predicate

- `dotnet build StreamsPlayer.sln -c Release` → 0 new warnings.
- `rg -n "ListeningHistory\.RecordPlay" src/StreamsPlayer.App` → exactly one hit, inside
  the `succeeded` branch of `RecordPlayOutcome` (verify no call on the failure path).
- `rg -n "ListeningHistory\.UpdateTrackText|PersistNowPlayingHistoryAsync" src/StreamsPlayer.App`
  → only in the now-playing path.
- `rg -n "RecordPlay|UpdateTrackText" src/StreamsPlayer.App/GridPreviewCoordinator.cs src/StreamsPlayer.App/VideoFrameCaptureService.cs`
  → no match (preview/probe never write history).
- Record `expected: build 0 new warn; RecordPlay only under succeeded; no preview writes | actual: ...`.

## Result — DONE

- `RecordPlayOutcome` now shares one `now` timestamp between the outcome mark and, under
  `if (succeeded)` only, `ListeningHistory.RecordPlay`. `OnNowPlayingTitle` fires
  `PersistNowPlayingHistoryAsync`, which calls `UpdateTrackText` and saves only when it
  returns non-null (entry present and text changed).
- expected: build 0 new warn; RecordPlay only under succeeded; no preview writes | actual:
  solution build 0 warn; one `ListeningHistory.RecordPlay` hit (MainWindow.xaml.cs, inside
  `succeeded`); 0 hits in GridPreviewCoordinator.cs / VideoFrameCaptureService.cs.
