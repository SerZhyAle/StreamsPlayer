# SP-0031 Tactical Plan - Channel preview pictures from the published atlas

Strategic spec: [../SP-0031_channel_preview_atlas.md](../SP-0031_channel_preview_atlas.md)
Research dossier: `temp/SP-0031/RESEARCH.md`

## Design

The atlas is consumed as a **one-shot bulk seeder of the existing preview store**, not as a live image
source. That is forced by a measured platform fact: WPF/WIC has no region decoder, so the first pixel read
of the 8160x7560 sheet materialises the whole frame (+265..491 MB). Android's per-tile `decodeRegion`
design (its ADR-2) therefore cannot be mirrored.

Pipeline, all of it on a background thread, once, on explicit user acceptance:

```
download .webp + .json  ->  parse coords (Core)  ->  decode sheet once (App/WIC)
   ->  for each catalog URL with a tile and no existing preview: crop 240x135, JPEG-encode, write
   ->  release sheet  ->  trim store once  ->  refresh visible tiles
```

Everything downstream already exists and is untouched: `PreviewFrameStore` (`{sha256(url)}.jpg` under
`grid-previews`, 150 MB LRU), `PreviewFrameCache` (64-entry LRU), `ChannelRow.TileImage => _preview ?? Favicon`.

Split across the dependency boundary:

- **Core** owns the *contract*: tile geometry, index -> rect, bounds validation, sidecar parsing, the asset
  URLs, and the download. All pure/IO-only, no imaging - so it stays platform-neutral and unit-testable.
- **App** owns the *imaging*: WIC decode, crop, JPEG encode, the store write, the offer UI.

Two traps the plan designs around explicitly:

1. **`PreviewFrameStore.SaveAsync` calls `TrimAsync` on every save**, and `TrimAsync` enumerates the whole
   directory. 1876 sequential saves would make that O(n^2) - tens of thousands of directory scans. Phase 3
   adds a bulk path that writes without trimming and trims exactly once at the end.
2. **A grid that cached its state before the atlas landed will not show it** (an owner-reported bug on the
   mobile side). Phase 4 re-queues the visible rows after a successful import.

## Phases (dependency order)

| # | Phase | Produces | Consumes |
| --- | --- | --- | --- |
| 1 | [Core contract + sidecar parser](01_core_contract.md) | `ChannelPreviewAtlas`, `ChannelPreviewCoords`, tests | - |
| 2 | [Core asset service + install marker](02_core_service.md) | `ChannelPreviewAtlasService`, `CatalogState.ChannelPreviewAtlasRevision` | 1 |
| 3 | [App importer + bulk store write](03_app_importer.md) | `ChannelPreviewAtlasImporter`, `PreviewFrameStore` bulk path | 1, 2 |
| 4 | [Offer UI, wiring, localization](04_offer_ui.md) | offer bar, post-refresh hook, post-import grid refresh | 3 |
| 5 | [Verify](05_verify.md) | run-and-observe evidence | 4 |

## Criterion -> phase coverage

| AC | Phase |
| --- | --- |
| 1 real frames for most video channels without connecting | 3, 4, 5 |
| 2 audio + untiled video unchanged | 1 (coords only), 3 (skip), 5 |
| 3 a captured frame is never replaced by a canned one | 3 (skip-if-exists), 5 |
| 4 "Refresh previews" still captures and wins | 3 (store overwrite unchanged), 5 |
| 5 memory returns, app stays responsive | 3 (scoped decode, background thread), 5 |
| 6 missing codec degrades invisibly | 3 (decode-failure path), 4 (plain message), 5 |
| 7 pictures survive restart + catalog update | 3 (same store), 5 |
| 8 nothing downloads unless asked | 2, 4 (offer gate) |

## Non-goals guardrails (from the spec)

- Core gains **no** imaging dependency - geometry and parsing only; every `BitmapSource` stays in App.
- No automatic/background download: the only call site is the accepted offer.
- The sheet is never retained after the import returns, and never touched on the UI thread.
- The local capture path, its throttles, and its gating are not modified.
- No change to `stream-catalog.zip` handling, the merge contract, or MANUAL/IMPORTED protection.
