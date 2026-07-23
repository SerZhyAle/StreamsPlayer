# SP-0016: M3U import and export portability

**Status:** Implemented — BlockNeedUserTest (file-picker run-and-observe, AC6)

## Verification (2026-07-22)

- `dotnet build StreamsPlayer.sln` → expected: succeeds | actual: succeeds, 0 warnings.
- `dotnet test StreamsPlayer.Core.Tests -c Release` → expected: green | actual: 108 passed, 0 failed
  (adds M3U analyze/writer/service + credential-probe cases).
- Localization en/ru key parity → expected: identical | actual: 226 == 226, no diff.
- App launch (after relocating entry points into Settings) → expected: starts, toolbar no longer shows
  Import/Export, no XAML/binding error | actual: started, loaded 2182 channels, clean `Current.log`, no exceptions.
- **Open (BlockNeedUserTest):** interactive file-picker round-trip cannot be driven headlessly here. Manual
  checklist for the user — all entry points now under **Settings → Playlists (M3U)**, fixtures under `tmp/sp0016/`:
  1. Settings → Import from file → `tmp/sp0016/sample.m3u`: preview shows New 2 (SomaFM audio + the
     `x36xhzz.m3u8` video stream URL), Duplicate 0 (2 if re-run after applying), Invalid 1 (`not-a-real-url`),
     Repeated 1 (the second SomaFM line); Apply adds both new channels as IMPORTED.
  2. Settings → Import from file → `tmp/sp0016/hls-manifest.m3u8`: "HLS media manifest" explanation, zero added.
  3. Settings → Export all my channels → save `.m3u`; re-import the saved file: titles and order preserved.
  4. Add a manual channel with `?token=…` or `user:pass@`, Settings → Export → credential warning before write.



## Scope decisions (2026-07-22)

- **Export scope for this ticket:** all user-owned rows (`SourceOrigin.Manual` + `Imported`), with a
  "Pinned only" variant. AC5's "named collection" export is deferred to SP-0017 (named collections are not
  yet implemented); the export writer and UI are collection-ready but no collection picker ships here.
- **Import dedup keying:** exact ordinal `Url`, matching the authoritative add-flow (`AddButton_Click`) and
  `CatalogMerger` contract, not `CatalogUrlIdentity.Normalize`.
- **Encoding:** import decodes bytes as strict UTF-8 (`new UTF8Encoding(false, true)`); non-UTF-8 input is
  reported as invalid and leaves state unchanged. Export writes UTF-8 without BOM.
- **Entry points:** a "Playlists (M3U)" section in the Settings window with four buttons — Import from file,
  Import from URL, Export all, Export pinned (relocated from the main toolbar at user request 2026-07-22).
  MainWindow owns the logic; Settings invokes it via a `RunStreamListPortabilityAsync(action, owner)`
  callback so every picker/preview/dialog is owned by the Settings window.
- **Preview categories:** New (added), Duplicate (URL already in the list), Invalid (not a launchable
  http/https/rtsp URL), Skipped (launchable URL repeated within the imported file).
- Tactical plan: [SP-0016_m3u_portability/INDEX.md](SP-0016_m3u_portability/INDEX.md).

## Goal

Let users bring compatible M3U channel lists into StreamsPlayer and export their own curated channels or collections without requiring an account.

## Why

M3U is a practical interchange format across stream players. Portable lists protect user effort and make StreamsPlayer easier to adopt while preserving its local-first model.

## Non-goals

- Download media for offline use.
- Treat an HLS media manifest as a list of channels.
- Export the full third-party catalog or redistribute its metadata.
- Synchronize lists automatically between devices.

## Constraints

- Import accepts a user-selected local `.m3u`/`.m3u8` file or an explicit HTTP(S) playlist URL.
- The existing playlist parsing, URL validation, provenance, URL de-duplication, and user-row-wins contracts remain authoritative.
- An input containing `#EXT-X-` is treated as an HLS manifest and imports zero channels with a clear explanation.
- Import is atomic and previews counts for new, duplicate, invalid, and skipped entries before applying changes.
- Export is limited to `MANUAL`/`IMPORTED` rows chosen directly, through favorites, or through one named collection; catalog-only rows are not exported.
- Exported credential-bearing URLs require an explicit warning before they are written in clear text.

## Acceptance criteria

1. Valid local and remote M3U lists import channel titles and launchable URLs with `IMPORTED` provenance.
2. Duplicate URLs do not create duplicate rows or overwrite existing `MANUAL`/`IMPORTED` data.
3. HLS manifests, inaccessible sources, invalid encoding, invalid URLs, and empty lists leave current state unchanged and explain the outcome.
4. Before applying a valid import, the user sees accurate category counts and can cancel without changing state.
5. The user can export selected user-owned channels, favorites, or a named collection to a valid UTF-8 M3U file and re-import it without losing titles or order.
6. Import/export contract tests and a Windows file-picker run-and-observe check pass.

## Risks

M3U files have inconsistent encodings and informal extensions. Export can expose embedded credentials, so the application must warn rather than silently leaking them.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md) and the playlist rules in [streams specification](../docs/specifications/streams.txt).
