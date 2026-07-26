# Phase 1 - Core contract: parse, carry, persist `access`

**Produces:** `ChannelAccess` enum, `CatalogEntry.Access`, `StreamChannel.Access`, CSV parsing, merge propagation.
**Consumes:** nothing.

## Steps

### 1.1 Add the `ChannelAccess` enum

`src/StreamsPlayer.Core/Models.cs` - add next to the other catalog enums:

```csharp
public enum ChannelAccess
{
    Open,
    GeoRestricted
}
```

`Open` must be the first member so it is the default for a value absent from an older state file
(AC 7), and so an unrecognised upstream value maps to "say nothing" (Decision 2).

### 1.2 Carry the value on the catalog entry

`src/StreamsPlayer.Core/Models.cs` - append to `CatalogEntry` as a trailing optional parameter:
`ChannelAccess Access = ChannelAccess.Open`. It must be appended after `IsLive`, not inserted, so
every existing positional construction site keeps compiling unchanged.

### 1.3 Carry the value on the channel

`src/StreamsPlayer.Core/Models.cs` - add to `StreamChannel`, in the untrusted-catalog-metadata block
next to `Protocol`/`Format`/`Bitrate`/`IsLive`:
`public ChannelAccess Access { get; init; } = ChannelAccess.Open;`

Note in the comment that this is a maintainer heuristic from one network vantage point, never a
playback decision.

### 1.4 Parse the column

`src/StreamsPlayer.Core/StreamCatalogCsvParser.cs` - add a private `ParseAccess` helper mirroring the
existing `ParseIsLive` shape, and pass its result as the new trailing `CatalogEntry` argument:

```csharp
private static ChannelAccess ParseAccess(string value) => value.Trim().ToLowerInvariant() switch
{
    "geo" => ChannelAccess.GeoRestricted,
    _ => ChannelAccess.Open
};
```

An absent column, an empty cell, and any unrecognised future value all yield `Open`.

### 1.5 Propagate through the merge

`src/StreamsPlayer.Core/CatalogMerger.cs` - add `Access = entry.Access` to **both** the `replacement`
`with` expression and the new-`StreamChannel` initializer. Do not touch the
`current.SourceOrigin != SourceOrigin.Catalog` guard: MANUAL and IMPORTED rows keep their default
`Open` and are never updated from the catalog.

Because `replacement != current` drives the `updated` counter, a refresh over an unchanged row still
produces an equal record and does not churn state (constraint).

### 1.6 Tests

`tests/StreamsPlayer.Core.Tests/StreamCatalogCsvParserTests.cs` - add cases: `access` = `geo` yields
`GeoRestricted`; empty, absent column, and an unrecognised value (e.g. `paywall`) each yield `Open`.

`tests/StreamsPlayer.Core.Tests/CatalogMergerTests.cs` - add cases: a catalog row's `Access` reaches a
newly added channel and updates an existing catalog-origin channel; a `Manual` row keeps `Open` when
the catalog claims `geo` for the same URL.

`tests/StreamsPlayer.Core.Tests/StreamCatalogStoreTests.cs` - add a round-trip case: a channel saved
with `GeoRestricted` loads back as `GeoRestricted`, and a state JSON written **without** the property
loads as `Open` without error (AC 7 - no migration).

## Static check

`dotnet test tests/StreamsPlayer.Core.Tests -c Release`

expected: all tests pass including the new parse, merge, and persistence cases | actual: 220/220 passed
(was 194 before this phase; +26 covering `geo`/case/whitespace/unknown/blank/missing-column parsing,
merge add + clear + Manual protection, and the save round-trip plus legacy-JSON default).

**Status: complete.**
