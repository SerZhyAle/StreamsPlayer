# PHASE-4 — App: manage-hidden window and unhide

**Consumes:** Phase 1 (`CatalogState.HiddenCatalogUrls`, `CatalogUrlIdentity`), Phase 2 (exclusion + hide op).
**Produces:** the dedicated view to inspect and unhide hidden catalog channels, and absent-from-catalog cleanup. Satisfies AC4.

## Steps

1. **`HiddenChannelsWindow`** — new `src/StreamsPlayer.App/HiddenChannelsWindow.xaml` (+ `.xaml.cs`), modelled on `AddStreamWindow`/`SettingsWindow` (UI-only, focused).
   - Input: current `CatalogState` (or the hidden URLs + the catalog channels to resolve titles).
   - List each hidden **catalog** row (resolved by matching `HiddenCatalogUrls` to `_state.Channels` catalog rows via `CatalogUrlIdentity`), showing title + redacted URL, each with an **Unhide** action.
   - Output: expose the resulting set of URLs to unhide (or raise an unhide callback per row) so `MainWindow` persists via `_store.SaveAsync`.
   - Empty state: localized "no hidden channels" message.

2. **Unhide operation** — new `MainWindow` method (in `MainWindow.Hide.cs`).
   - `UnhideAsync(string url)`: `_state = await _store.SaveAsync(_state with { HiddenCatalogUrls = _state.HiddenCatalogUrls.Where(u => !CatalogUrlIdentity.SameIdentity(u, url)).ToList() })`; then `PopulateFacets(); ApplyFilter();`.
   - Unhide restores the **current** catalog row as-is: it never writes pin/order/collection/play-mark, so those are preserved by construction (hide/unhide only ever touch `HiddenCatalogUrls`, never the channel record).

3. **Absent-from-catalog cleanup** — a hidden URL whose catalog row is no longer present (removed by a later refresh's prune) must not error: the manage-hidden list simply skips unresolved URLs, and an unhide of an unresolved URL still just drops it from `HiddenCatalogUrls` without touching `_state.Channels`. Optionally prune orphan hidden URLs opportunistically after a refresh (document if deferred).

4. **Entry point** — add a way to open `HiddenChannelsWindow` from `MainWindow` (a button or an item in the existing overflow/settings surface). Keep it discoverable but out of the primary flow.
   - Localization (both en + ru): `ManageHiddenTitle`, `ManageHiddenOpen` (entry label), `HiddenChannelsEmpty`, `MenuUnhide`/`UnhideButton`, and a hidden-count status key if shown.

## Static verification predicate

- `dotnet build StreamsPlayer.sln` succeeds with no new warnings.
- `rg` confirms `HiddenChannelsWindow` is constructed from `MainWindow`, `UnhideAsync` only mutates `HiddenCatalogUrls` (never `_state.Channels`), and new keys exist in **both** dictionaries (parity).
- Record `expected: build ok; unhide touches only HiddenCatalogUrls; key parity | actual: ...`.
- Visible view/unhide behaviour proven in Phase 5 GUI observation.

## Result — DONE

- New `HiddenChannelsWindow` (XAML + code-behind) with `HiddenChannelView(Title, RedactedUrl, Url)`, an `ObservableCollection` list + per-row Unhide and an empty state. `MainWindow.Hide.cs` gained `HiddenChannelsButton_Click` (resolves hidden catalog rows, redacted URLs) and `UnhideAsync` (mutates only `HiddenCatalogUrls`).
- Entry point: `HiddenGlyphButton` (eye-off glyph) toolbar button after Settings, shown only when `HiddenCatalogUrls.Count > 0` (toggled in `ApplyFilter`). Added 5 keys to en + ru (`ManageHiddenTitle`, `ManageHiddenOpen`, `ManageHiddenTip`, `HiddenChannelsEmpty`, `Unhide`).
- Orphan hidden URLs (catalog row pruned by a later refresh) are simply not listed and cause no error; opportunistic orphan pruning deferred (harmless).
- expected: Release build clean | actual: build 0 warnings / 0 errors.
