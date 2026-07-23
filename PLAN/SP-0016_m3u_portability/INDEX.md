# SP-0016 Tactical Plan — M3U import/export portability

Strategic spec: [../SP-0016_m3u_portability.md](../SP-0016_m3u_portability.md)

## Current state (verified in working tree)

- `M3uPlaylistParser.Parse` already exists (import line-scan, `#EXT-X-` HLS guard, `IsLaunchable` gate,
  host-fallback title, in-file dedup). Only unit-tested; **no App caller**. Safe to refactor.
- No export writer, no remote-import service, no file picker anywhere. `OpenFileDialog`/`SaveFileDialog`
  are unused (in-box `Microsoft.Win32`, no new dependency).
- `StreamChannel` (Models.cs) has `Url`, `Title`, `SourceOrigin`, `SortIndex`, `Pinned`. `SourceOrigin`
  = Catalog/Manual/Imported. `CatalogUrlIdentity` has `Redact` + credential-key list.
- App: `MainWindow` owns `_httpClient`, `_store`, `_state`, `SetBusy/SetStatus`, `PopulateFacets/ApplyFilter`.
  Toolbar buttons at `MainWindow.xaml` lines 43-48. `HiddenChannelsWindow` is the small-dialog model.

## Dependency-ordered phases

Each step's predicate must pass in the same run before the step is done.

1. **P1 — Core import analysis.** `M3uImportPreview` record + `M3uPlaylistParser.Analyze(text, existingUrls)`;
   refactor `Parse` to delegate. Predicate: `dotnet build src/StreamsPlayer.Core` succeeds; existing
   `M3uPlaylistParserTests` still compile.
2. **P2 — Core export writer + credential probe.** `M3uPlaylistWriter.Write(channels)` and
   `CatalogUrlIdentity.HasCredentials(url)`. Predicate: Core builds.
3. **P3 — Core remote/encoding service.** `M3uImportService` (injected `HttpClient`): `FetchAsync(url)` +
   static `DecodeUtf8(bytes)` (strict UTF-8, BOM-strip, throws on invalid). Predicate: Core builds.
4. **P4 — Core tests.** Extend `M3uPlaylistParserTests`; add `M3uPlaylistWriterTests`,
   `M3uImportServiceTests` (DecodeUtf8 valid/invalid/BOM), credential-probe cases. Predicate:
   `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "M3u|CatalogUrlIdentity"` green.
5. **P5 — App import UI.** Two glyph styles (`ImportGlyphButton`/`ExportGlyphButton`) in App.xaml;
   `MainWindow.ImportExport.cs` partial: `ImportUrlWindow` + preview dialog `ImportPreviewWindow`, atomic
   single `SaveAsync` stamping `SourceOrigin.Imported`, `SortIndex` continuing from max. Predicate:
   `dotnet build StreamsPlayer.sln` succeeds.
6. **P6 — App export UI + credential warning.** `SaveFileDialog`, UTF-8 no-BOM write, credential `MessageBox`
   gate. Predicate: solution builds.
   - **Entry point (revised 2026-07-22):** a "Playlists (M3U)" section in the Settings window with four
     buttons (not toolbar buttons); MainWindow exposes `RunStreamListPortabilityAsync(action, owner)` and
     Settings invokes it so all dialogs are owned by the Settings window.
7. **P7 — Localization.** Add en + ru strings for every new `DynamicResource`/`LocalizationService.Get`
   key. Predicate: no missing-key at runtime (grep parity en vs ru for the new keys).
8. **P8 — Validate.** `dotnet build StreamsPlayer.sln`; focused Core tests; run the app and run-and-observe:
   import a local `.m3u` (preview counts + apply), an HLS manifest (zero + explanation), export to file and
   re-import (titles + order preserved), export a credential URL (warning). Record `expected|actual`.

## Contracts to preserve

- Import never overwrites/prunes existing rows; only additive `Imported` inserts (do **not** reuse
  `CatalogMerger.Merge`).
- Core stays WPF-free; file pickers + dialogs live in App only.
- Explicit action only; no background download.
