# SP-0030 Tactical Plan - Delete all downloaded catalog streams

Strategic spec: [../SP-0030_delete_downloaded_catalog_streams.md](../SP-0030_delete_downloaded_catalog_streams.md)

## Design

A pure Core rule plus a thin App action.

- **Core** gains `CatalogPurge` (new file, static, mirrors `CatalogMerger`'s shape): counts
  `SourceOrigin.Catalog` rows and returns a `CatalogState` without them. Nothing else in the
  state record is touched - `HiddenCatalogUrls`, `AtlasFileName`, `LastCatalogRefreshAt`,
  history, and preferences ride through unchanged (Decisions 4 and 5).
- **App** reuses the existing Settings → MainWindow callback that already runs the M3U actions.
  The enum is renamed from `StreamListPortabilityAction` to `StreamListAction` so a delete
  action is not filed under "portability"; the four existing members keep their names.
- The action lives in a new partial `MainWindow.CatalogPurge.cs`: count → early-out message when
  zero → Yes/No confirmation carrying the count → save → `ForgetRow` for every removed id (this
  drops row cache entries, clears the selection, and stops inline audio for a removed channel)
  → `PopulateFacets` + `ApplyFilter` → log + status.
- `ApplyFilter` currently shows the **Hidden** button whenever `HiddenCatalogUrls` is non-empty.
  After a purge that set can reference rows that no longer exist, which would open an empty
  window. Visibility becomes "at least one currently loaded row is hidden".

## Phases (dependency order)

1. **Phase 1 - Core rule + tests.** New `src/StreamsPlayer.Core/CatalogPurge.cs`;
   `tests/StreamsPlayer.Core.Tests/CatalogPurgeTests.cs` covering: catalog rows removed,
   Manual/Imported (and their pins) kept, unrelated state fields preserved, no-catalog-rows
   no-op. Static check: `dotnet test tests/StreamsPlayer.Core.Tests -c Debug` green.
2. **Phase 2 - App action.** Rename the action enum and dispatcher; add
   `MainWindow.CatalogPurge.cs`; adjust the Hidden-button visibility rule. Static check:
   solution builds with no CS errors.
3. **Phase 3 - Settings UI + localization.** Trash glyph style in `App.xaml`, separated button
   row at the bottom of the Playlists tab, handler in `SettingsWindow.xaml.cs`, six English and
   six Russian strings. Static check: `./build.ps1 -Test` green.
4. **Phase 4 - Docs.** One feature bullet in `README.md`, `README.ru.md`, `README.uk.md`.
   Static check: bullet present in all three.
5. **Phase 5 - Verify.** Run the app: decline the confirmation (nothing changes), accept it
   (catalog rows gone, user rows and pins kept), restart (still gone). Record
   `expected | actual` for each.

## Guardrails (from the spec)

- No change to `CatalogMerger`, `StreamCatalogService`, or the explicit-refresh contract.
- Core stays platform-neutral; the App holds all UI, confirmation, and logging.
- No automatic invocation of the purge from any code path other than the Settings button.
