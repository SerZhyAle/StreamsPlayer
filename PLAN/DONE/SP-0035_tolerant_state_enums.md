# SP-0035: Tolerant enum handling across the whole persisted state

**Status:** Verified

## Problem

`StreamCatalogStore.LoadAsync` deserializes `catalog-state.json` with no per-field tolerance. Any enum
value the running build does not recognise makes `System.Text.Json` throw for the **whole document**,
and the App then leaves `_state` at its empty initialiser and shows an empty catalog.

SP-0034 fixed exactly one field. `CatalogState` still deserializes four more enums the same way:

- `ViewMode` (`CatalogViewMode`)
- `TileSize` (`StreamTileSize`)
- `VideoBackend` (`VideoBackendKind`)
- `Access` on each `StreamChannel` (`ChannelAccess`) - note the CSV path already degrades an unknown
  token to `Open` (`StreamCatalogCsvParser.cs:73-77`), but the JSON state path does not, so a value
  that reached the state before a rename is unrecoverable.

The trigger is the same one SP-0034 documented: a state file written by a newer build and read by an
older one. Adding any member to any of these enums makes it reachable, and each of them is cheaper to
add than a language, so this will happen again without a general fix.

## Approach

Make unknown enum values a *lost field*, not a corrupt document, for every enum in the persisted state
rather than one at a time - a generic tolerant converter registered once, degrading to the property's
default and never throwing. `TolerantAppLanguageConverter` (SP-0034) is the single-field precedent and
should be folded into it or kept as the nullable special case.

Consider separately whether an unreadable `catalog-state.json` should be quarantined - renamed aside
rather than overwritten - so a document-level failure from any other cause also stops costing the user
their catalog. SP-0034 removed the language trigger but not the class of failure.

## Done criteria

- A state file naming an unknown value for any persisted enum loads successfully, with that one field
  at its default and every other field intact.
- Numeric values outside the defined members are rejected rather than silently cast and persisted.
- Tests cover each enum in `CatalogState` with an unknown name, an out-of-range number and a missing
  property, asserting that channels, collections, history and window preferences survive.

## Notes

Parked out of SP-0034, whose scope was the language field only (its Decision 7). Discovered while
auditing that ticket on 2026-07-27; see its `## Last Audit` finding A for the data-loss mechanism.

## Resolution (2026-07-28)

`TolerantEnumConverter<TEnum>` and its optional sibling `TolerantNullableEnumConverter<TEnum>` are
registered once in `StreamCatalogStore`'s serializer options, ahead of `JsonStringEnumConverter`, and
cover every enum the state persists: `ViewMode`, `TileSize`, `VideoBackend`, `MediaKind`,
`SourceOrigin`, `Access` and the optional `LastPlayOutcome`. An unknown name, an out-of-range number,
an explicit null, or structural garbage costs that one field and nothing else; the reader is left
positioned so the rest of the document deserializes normally.

Two fallbacks are chosen to protect data rather than to restore a default:

- an unreadable `SourceOrigin` reads as `Manual`, the origin a catalog refresh never rewrites or prunes,
  so a row whose origin was lost cannot be deleted by the next refresh;
- an unreadable `MediaKind` reads as `Video`, whose player also handles audio and RTSP.

`LastPlayOutcome` is optional, so an unreadable value reads as absent instead of inventing a recorded
failure. Writing is unchanged - every enum still persists by member name - and a value outside the enum
is normalized on write, so this build never plants a token it cannot read back.

Not done here, and still open: quarantining an unreadable `catalog-state.json` (the "consider
separately" paragraph above). This ticket removed the enum triggers, not the class of failure.

## Verification (2026-07-28)

- `./scripts/check.ps1` (Release restore + build + test) - expected: green, including the new
  `CatalogStateEnumToleranceTests` (7 unreadable shapes x 7 enums, plus mixed, missing-property and
  round-trip facts) | actual: `Total tests: 381, Passed: 381`, 0 warnings, 0 errors.
- expected: one unreadable enum leaves every other enum, the channel list, collections, history, hidden
  URLs and window preferences intact | actual: asserted by `AssertUserDataSurvived` in every case.
