# Phase 2 - Core asset service + install marker

Consumes Phase 1. Adds the download and the "already seeded" state, still with no imaging.

## Steps

1. **New `src/StreamsPlayer.Core/ChannelPreviewAtlasService.cs`.**
   - `public const string AtlasUrl` / `CoordsUrl` - the two `delivery-so-v1` assets, and
     `public const string Revision = "v1"` matching the `-v1` element suffix pinned in both URLs. A comment
     records that a non-tile-compatible rebuild ships under a new suffix and nothing auto-upgrades.
   - `DownloadAsync(CancellationToken)` -> `record ChannelPreviewAtlasPayload(byte[] Sheet, IReadOnlyDictionary<string,int> Coords)`.
     Fetches the sidecar first (135 KB - a cheap failure) then the sheet (~11 MB, README ceiling 30 MB),
     parses coords through Phase 1, and returns both.
   - Reuses the injected `HttpClient` like `StreamCatalogService`; own linked `CancellationTokenSource`
     deadline sized for the sheet, not the 30 s catalog deadline.
   - Rejects a sheet larger than a `MaximumSheetBytes` ceiling so a mispublished asset cannot be pulled
     into memory unbounded.
   - Static check: `dotnet build src/StreamsPlayer.Core -c Release` succeeds; grep confirms no
     `System.Windows`/imaging using in Core.

2. **`src/StreamsPlayer.Core/Models.cs` - `CatalogState.ChannelPreviewAtlasRevision`** (`string?`, default
   `null`). Set to `ChannelPreviewAtlasService.Revision` only after a **successful** import. Meaning:
   - `null` -> never seeded, the offer is eligible;
   - equal to the current `Revision` -> seeded, never offer;
   - different -> a new element revision exists, the offer is eligible again.
   - Static check: build; `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~StreamCatalogStoreTests"`
     - expected: the existing 9 store tests still pass (the record gained an optional field).

3. **Extend `tests/StreamsPlayer.Core.Tests/StreamCatalogStoreTests.cs`** with a round-trip of
   `ChannelPreviewAtlasRevision` alongside the other persisted preferences.
   - Static check: same filtered run - expected: 10 tests pass.

## Stop conditions

- If either asset URL does not return HTTP 200 at implementation time, stop: the feature has no payload.
  (Verified 2026-07-26: both 200, 11 358 632 B and 134 997 B.)
- Do **not** call `DownloadAsync` from any load/refresh path in this phase. It stays unreferenced until
  Phase 4 wires the accepted offer - AC 8.
