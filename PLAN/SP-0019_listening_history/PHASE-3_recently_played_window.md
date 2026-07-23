# PHASE-3 — Recently-played window, toolbar entry, Play / Clear

Produces: `ListeningHistoryWindow` + `MainWindow.History.cs` + toolbar button.
Consumes: Phases 1–2. Modelled on `HiddenChannelsWindow` / `MainWindow.Hide.cs`.

## Steps

1. **`ListeningHistoryWindow`** — new
   [src/StreamsPlayer.App/ListeningHistoryWindow.xaml](../../src/StreamsPlayer.App/ListeningHistoryWindow.xaml)
   (+ `.xaml.cs`), mirroring `HiddenChannelsWindow` (UI-only, focused):
   - `internal sealed record HistoryRowView(Guid ChannelId, string Title, string PlayedAt, string? Track, bool Playable);`
   - `ObservableCollection<HistoryRowView>` bound to a `ListBox`; per row: title
     (`SemiBold`), a muted second line = `PlayedAt` plus `Track` when present, and a
     **Play** button `IsEnabled="{Binding Playable}"` (Tag=row) — a non-resolvable id
     renders as a dimmed, non-playable label (`Track`/hint via `HistoryUnavailable`).
   - Footer: **Clear history** button (left) + **Close** (`IsCancel`, right).
   - Empty-state `TextBlock` (`HistoryEmpty`) toggled like `HiddenChannelsWindow`.
   - Ctor `internal ListeningHistoryWindow(IReadOnlyList<HistoryRowView> rows, Func<Guid, Task> play, Func<Task> clear)`;
     Play removes nothing locally (playback may reorder history on success → handled on
     reopen); Clear empties the collection, calls `_clear()`, refreshes empty-state, and
     closes. All strings via `DynamicResource` (live EN⇄RU).

2. **`MainWindow.History.cs`** — new partial:
   - `HistoryButton_Click`: build rows from `_state.ListeningHistory` in order; for each,
     `var live = _state.Channels.FirstOrDefault(c => c.Id == e.ChannelId);` set
     `Playable = live is not null`, prefer `live?.Title ?? e.Title` for display, format
     `PlayedAt` = `e.LastPlayedAt.ToLocalTime()` via localized `HistoryPlayedAt` (`{0:g}`),
     pass `e.LastTrackText`. Open `new ListeningHistoryWindow(rows, PlayFromHistoryAsync, ClearHistoryAsync) { Owner = this }; window.ShowDialog();`.
   - `PlayFromHistoryAsync(Guid id)`: `var channel = _state.Channels.FirstOrDefault(c => c.Id == id);`
     if `channel is null` → `SetStatus("HistoryUnavailable")` and return (fail soft, **no**
     playback, no stale URL); else `await PlayChannelAsync(channel, rememberSelection: true);`.
   - `ClearHistoryAsync()`: `_state = await _store.SaveAsync(_state with { ListeningHistory = [] });`
     — touches only `ListeningHistory`; channels, pins, collections, play-marks, hidden
     set, and catalog data are untouched (AC5).

3. **Toolbar entry** — in
   [MainWindow.xaml](../../src/StreamsPlayer.App/MainWindow.xaml) add a
   `HistoryButton` (`Style="{StaticResource HistoryGlyphButton}"`, `Content="{DynamicResource HistoryOpen}"`,
   `Click="HistoryButton_Click"`, tooltip/automation `HistoryTip`) beside `SettingsButton`.
   Always visible (discoverable). Add a clock-glyph `HistoryGlyphButton` style in
   [App.xaml](../../src/StreamsPlayer.App/App.xaml) next to `HiddenGlyphButton`, e.g.
   `Figures="M12,4A8,8 0 1 0 12.01,4M12,7V12L15.5,14"`.

4. **Localization keys (both en + ru, added in Phase 4 parity check)** — used here:
   `HistoryTitle` (window title, "Recently played"), `HistoryOpen` (button, "History"),
   `HistoryTip`, `HistoryEmpty`, `HistoryClear`, `HistoryPlayedAt` (`{0:g}`),
   `HistoryUnavailable` (non-playable/fail-soft). Reuse `Play`, `Close`.

## Static verification predicate

- `dotnet build StreamsPlayer.sln -c Release` → 0 new warnings.
- `rg -n "PlayFromHistoryAsync" src/StreamsPlayer.App/MainWindow.History.cs` shows it
  resolves via `_state.Channels.FirstOrDefault(... Id ...)` and has **no** `new Uri` /
  `channel.Url` / `CreateExternalChannel` path (no stale-URL reopen).
- `rg -n "ListeningHistory = \[\]" src/StreamsPlayer.App/MainWindow.History.cs` → the only
  mutation in `ClearHistoryAsync`, touching nothing else.
- `rg -n "ListeningHistoryWindow" src/StreamsPlayer.App` → constructed once from `MainWindow`.
- Record `expected: build ok; play resolves by id only; clear touches only history | actual: ...`.
- Visible behaviour proven in Phase 4 GUI observation.

## Result — DONE

- New `ListeningHistoryWindow` (`.xaml`/`.xaml.cs`) with `HistoryRowView`
  (Title/PlayedAt/Track + computed `TrackVisibility`/`UnavailableVisibility`/`RowOpacity`),
  per-row Play (disabled when unresolved), Clear-with-confirm, empty state, Close.
  `MainWindow.History.cs`: `HistoryButton_Click` (resolves each id to set Playable and a
  fresh title), `PlayFromHistoryAsync` (id-only; unresolved → `SetStatus("HistoryUnavailable")`),
  `ClearHistoryAsync` (`ListeningHistory = []`). Toolbar `HistoryButton` before Settings;
  `HistoryGlyphButton` uses a custom outline-clock template (`Fill="Transparent"`) because
  the shared `GlyphButton` fills the glyph with Foreground.
- expected: build ok; play by id only; clear touches only history | actual: solution build
  0 warn; `MainWindow.History.cs` has no `Uri`/`Url`/`CreateExternalChannel`;
  `ListeningHistory = []` only in `ClearHistoryAsync`; window constructed once.
