# PHASE-1 — Core persisted backend setting

**Produces:** `MediaBackend` enum, `CatalogState.VideoBackend` (default `LibVlc`), Core coverage.
**Consumes:** nothing.
**Goal:** the selectable choice persists alongside existing preferences and defaults to LibVLC,
with Core still holding **zero** media dependency (an enum is a plain value, not a media type).

## Steps

1. **Add the enum.** In [src/StreamsPlayer.Core/Models.cs](../../src/StreamsPlayer.Core/Models.cs),
   beside the other UI enums (near `StreamTileSize`, line ~77), add:
   ```csharp
   public enum MediaBackend
   {
       LibVlc,
       Flyleaf
   }
   ```
   `LibVlc` is first so it is the enum default (0) — matters for any state that predates the field.

2. **Add the persisted field.** In the `CatalogState` record (Models.cs ~line 150), beside
   `TileSize` / `UpdateStreamPreviews`, add:
   ```csharp
   /// <summary>
   /// Playback engine for the video/RTSP player window only (SP-0026). Defaults to
   /// <see cref="MediaBackend.LibVlc"/> — the proven baseline; <see cref="MediaBackend.Flyleaf"/>
   /// is an opt-in troubleshooting fallback. Audio and headless thumbnail capture ignore this.
   /// An older state file lacking this key deserializes to the LibVlc default.
   /// </summary>
   public MediaBackend VideoBackend { get; init; } = MediaBackend.LibVlc;
   ```
   Persistence needs no store change: `CatalogState` already serializes via
   `JsonStringEnumConverter` (same pattern as `TileSize`, `ViewMode`, `Language`).

3. **Core test — default + round-trip.** In
   [tests/StreamsPlayer.Core.Tests](../../tests/StreamsPlayer.Core.Tests) add a test (new
   `MediaBackendStateTests.cs`, or extend the existing state/serialization test file if one owns
   `CatalogState` JSON — locate with `rg "VideoBackend|CatalogState" tests`):
   - a default-constructed `CatalogState` has `VideoBackend == MediaBackend.LibVlc`;
   - serializing `state with { VideoBackend = MediaBackend.Flyleaf }` and deserializing round-trips
     to `Flyleaf`, and the JSON contains the string `"Flyleaf"` (enum-as-string contract);
   - JSON that omits the key deserializes to `LibVlc` (backward-compatible default).

## Static check

- `dotnet test tests/StreamsPlayer.Core.Tests -c Release` → **expected:** build succeeds, all tests
  pass including the new backend-state test | **actual:** _record on execution._
- Grep guard — Core stays media-free: `rg -i "libvlc|flyleaf|mediaplayer|videoview" src/StreamsPlayer.Core`
  → **expected:** no matches | **actual:** _record on execution._
