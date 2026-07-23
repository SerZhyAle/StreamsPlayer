# PHASE-2 — App: origin-aware Remove operations and hidden exclusion in the filter

**Consumes:** Phase 1 (`CatalogState.HiddenCatalogUrls`, `CatalogUrlIdentity`).
**Produces:** `HideCatalogChannelAsync`, `DeleteUserChannelAsync`, an origin-aware `RemoveChannelAsync` router, and hidden-row exclusion in `ApplyFilter` — consumed by Phases 3–4.

All edits in `src/StreamsPlayer.App/MainWindow.xaml.cs` (or a new `MainWindow.Hide.cs` partial if the file nears the ~500-line budget — it is currently ~732 lines, so **create `MainWindow.Hide.cs`**). No Core changes here.

## Steps

1. **Exclude hidden catalog rows from display** — `ApplyFilter` [MainWindow.xaml.cs:289](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L289).
   - In the `_state.Channels.Where(...)` predicate, add: exclude any `SourceOrigin.Catalog` row where `CatalogUrlIdentity.IsHidden(_state.HiddenCatalogUrls, channel.Url)`. Manual/Imported never excluded by hide.
   - Fix counts: the total shown in `SetStatus("ChannelCount", Rows.Count, _state.Channels.Count)` must exclude hidden — use `_state.Channels.Count(...)` minus hidden, or a computed visible-universe count.

2. **Exclude hidden from facets** — `PopulateFacets` [MainWindow.xaml.cs:353](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L353) should build facet options from the non-hidden universe so a fully-hidden category/language/country stops appearing. Preview queue already draws from `Rows`/`GetVisibleRows`, so excluding at `ApplyFilter` also removes hidden rows from preview capture and navigation — verify no separate path enumerates `_state.Channels` for playback navigation.

3. **`HideCatalogChannelAsync(StreamChannel channel)`** — new (in `MainWindow.Hide.cs`).
   - Guard `channel.SourceOrigin == SourceOrigin.Catalog`.
   - `_state = await _store.SaveAsync(_state with { HiddenCatalogUrls = [.. _state.HiddenCatalogUrls, channel.Url] })` (dedupe by normalized identity before adding).
   - Drop the row from `_rowCache` (`_rowCache.Remove(channel.Id)`); if it is `_selectedRow`/`_playingAudio`, clear as appropriate.
   - `PopulateFacets(); ApplyFilter();` and `SetStatus("HiddenStream", …)`.

4. **`DeleteUserChannelAsync(StreamChannel channel)`** — new (in `MainWindow.Hide.cs`).
   - Guard `channel.SourceOrigin is SourceOrigin.Manual or SourceOrigin.Imported`.
   - Remove from state: `_state = await _store.SaveAsync(_state with { Channels = _state.Channels.Where(c => c.Id != channel.Id).ToList() })`. (Do not mutate via `ReplaceChannel`; produce a new list so no colliding-URL row is affected — match strictly by `Id`.)
   - `_rowCache.Remove(channel.Id)`; clear `_selectedRow`/`_playingAudio`/stop audio if it was the deleted one.
   - `PopulateFacets(); ApplyFilter();` and `SetStatus("DeletedStream", …)`.

5. **`RemoveChannelAsync(StreamChannel channel)`** — new origin router: Catalog → `HideCatalogChannelAsync`; Manual/Imported → `DeleteUserChannelAsync`. This is the single entry Phase 3's dialog calls. Confirmation UI lives in Phase 3; this method assumes the action is already confirmed.

## Static verification predicate

- `dotnet build StreamsPlayer.sln` succeeds with no new warnings.
- `rg` assertions: `ApplyFilter` references `CatalogUrlIdentity.IsHidden`; `RemoveChannelAsync` routes both origins; `DeleteUserChannelAsync` matches by `Id` only.
- Record `expected: build ok; grep confirms exclusion + routing | actual: ...`.
- Behavioural proof of hide/delete deferred to Phase 5 GUI observation (no App unit tests exist).

## Result — DONE

- New `MainWindow.Hide.cs` partial: `BuildHiddenIdentitySet`, `IsHiddenBySet`, `RemoveChannelAsync` (router), `HideCatalogChannelAsync`, `DeleteUserChannelAsync`, `ForgetRow`. `ApplyFilter` excludes hidden catalog rows and counts the visible universe; `PopulateFacets` builds facets from the non-hidden universe. Added `HiddenStream`/`DeletedStream` status keys to en + ru.
- Verified no browsing/navigation path bypasses the filter (`_state.Channels` direct uses are add/edit dup-checks, pin, outcome-by-id, and explicit launch-by-id shortcut only).
- expected: Release build clean | actual: build 0 warnings / 0 errors.
