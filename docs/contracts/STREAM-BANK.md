# STREAM-BANK - pointer

| | |
| --- | --- |
| **Id** | `STREAM-BANK` |
| **Version** | 2.1 (active) |
| **Home** | `Contracts/stream-catalog/README.md`, with the detail in `01_delivery_contract.md`, `03_catalog_format.md`, `04_favicon_atlas.md`, `09_logo_and_preview_atlases.md` and the dated amendments `10_` and `12_` |
| **Owner** | FastMediaSorter Android - StreamsPlayer neither publishes the bank nor amends the contract |
| **This product's role** | **consumer** of the published ZIP and of the on-demand artwork set |

## What this repository must do to stay conformant

The twelve rules are in the home page and are not repeated here. What is repo-local is which code holds
each of them, so a change can be routed:

| Rule | Held by |
| --- | --- |
| 1 explicit fetch | `StreamCatalogService` - no background refresh exists, and none may be added |
| 2 `streams.csv` first | `StreamBankReader` |
| 3 columns by header name | `StreamCatalogCsvParser` |
| 4 drop rows without `url`/`name`; `media_kind` routes | `StreamCatalogCsvParser`, `StreamMediaKindClassifier` |
| 5 merge by `url`, absence never deletes | `CatalogMerger`, `UserAuthoredChannels`, `StreamChannel.RetiredAt` |
| 6 CSV and atlas are one artifact | `CatalogRefreshOutcome` and `CatalogSnapshotOutcome` (the indices are cleared when no usable atlas arrived), then `CatalogMerger` |
| 7 blank `favicon_index` is no icon, `0` is a tile | `StreamCatalogCsvParser` |
| 8 bounds-check, degrade to nothing | `StreamBankReader.MaximumAtlasBytes`, the atlas and tile readers |
| 9 stable artwork names, geometry from the artifact | `ChannelPreviewArtworkService`, `ChannelPreviewTilePack`, `ChannelPreviewCoords` |
| 10 `access` is opaque | `StreamCatalogCsvParser` |
| 11 a publish-window 404 is expected | `PublishWindowRetry`, used by `StreamCatalogService` and `ChannelPreviewArtworkService` (SP-0107) |
| 12 invalidate on the manifest `stamp` alone | `ArtworkManifest`, `CatalogState.ChannelPreviewArtworkStamp` |

**Conformance evidence.** The contract names no vectors: the published `stream-catalog.zip` is the vector,
plus the minimal consumer checklist in `01_delivery_contract.md` section 8. The live check is
`dotnet run --project tools/StreamsPlayer.CatalogHarness`.

**Deviations** are recorded in the store's `_meta/REGISTRY.md`, never here.
