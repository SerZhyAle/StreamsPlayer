# SP-0018 Tactical Plan — Stream quality details and filtering

**Strategic ticket:** [../SP-0018_stream_quality_details.md](../SP-0018_stream_quality_details.md)
**Status:** Tactical

## Summary

Surface the optional maintainer metadata already present in the CSV contract
(`protocol`, `format`, `bitrate`, `is_live`) as compact per-card technical
details, and add a minimum-bitrate filter that excludes rows whose bitrate is
missing or unparseable while it is active. The default view is unchanged.

## Contract notes

- `streams.csv` already ships `protocol, format, bitrate, is_live` (source spec
  A.2). They are optional, untrusted maintainer claims — never gate visibility on
  them by default, never infer a playback decision or success mark from them
  (invariant, AC5 / streams.txt Part C.3).
- Persisted state gains four nullable `StreamChannel` fields. Older
  `catalog-state.json` files deserialize the missing keys to `null`; no
  `SchemaVersion` bump is required (all additions are nullable).
- The URL merge contract and MANUAL/IMPORTED protection are unchanged; the new
  fields ride along only on `SourceOrigin.Catalog` update/insert.

## Bitrate representation decision

`bitrate` is stored as the **raw trimmed claim string** on `CatalogEntry` and
`StreamChannel` (faithful to "untrusted claim, labelled accordingly"; lossless
display). Filtering and any numeric comparison go through one pure Core helper,
`StreamBitrate`, which tolerantly parses kbps. Missing **or** unparseable ⇒
excluded under an active minimum, visible under the default (AC3).

## Phases (dependency-ordered)

| Phase | Title | Produces | Consumes |
|-------|-------|----------|----------|
| [01](01_core_model_and_parser.md) | Core model + CSV parse | 4 optional fields on `CatalogEntry`/`StreamChannel`, parsed from CSV | — |
| [02](02_core_merge_and_bitrate.md) | Merge carry-through + bitrate helper | merge preserves new fields; `StreamBitrate` parse/filter helper | 01 |
| [03](03_app_details_presentation.md) | Card technical-details display | compact per-card details line + localization | 01 |
| [04](04_app_bitrate_filter.md) | Minimum-bitrate filter | filter control, state, apply, persistence, active indicator | 02 |
| [05](05_validation.md) | Localized run-and-observe | PASS/FAIL evidence for details + filter | 03, 04 |

## Criterion → phase coverage

- AC1 compact format/bitrate/protocol/live details → 03
- AC2 activate/clear min-bitrate filter, combines with search + facets → 04
- AC3 unknown/malformed bitrate visible by default, excluded under min → 02 (helper) + 04 (apply)
- AC4 refresh updates catalog tech metadata, preserves user/order state → 02
- AC5 no playback decision or success mark from metadata → invariant held across all phases; confirmed in 05
- AC6 parsing/filter tests + localized run-and-observe → 01 & 02 (tests) + 05

## Constraint → phase coverage

- Technical fields stay optional/untrusted/labelled → 01, 03
- Default view unchanged (known + unknown quality) → 04
- Active min filter excludes missing/unparseable + shows it is active → 04
- Present-only display with one quiet fallback → 03
- Catalog-declared media kind still wins → unchanged (01 leaves `FromCatalogValue` intact)
