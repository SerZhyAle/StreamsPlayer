# Phase 02 — Merge carry-through + bitrate helper

**Produces:** `CatalogMerger` preserves the four new fields on catalog
update/insert; `StreamBitrate` pure helper for kbps parse + minimum test.
**Consumes:** Phase 01 fields.

## Steps

1. [CatalogMerger.cs](../../src/StreamsPlayer.Core/CatalogMerger.cs): in the
   `current with { ... }` update block add
   `Protocol = entry.Protocol, Format = entry.Format, Bitrate = entry.Bitrate,
   IsLive = entry.IsLive`. In the new-`StreamChannel` initializer add the same
   four assignments from `entry`. User (`Manual`/`Imported`) rows are still
   `continue`d untouched — contract preserved.
2. New file `src/StreamsPlayer.Core/StreamBitrate.cs`, `public static class
   StreamBitrate`:
   - `bool TryParseKbps(string? raw, out int kbps)` — trim; accept a leading
     decimal number optionally followed by a unit token: bare / `k` / `kbps` /
     `kb` ⇒ value rounded to int kbps; `m` / `mbps` / `mb` ⇒ value × 1000.
     Reject null/empty/no-leading-number ⇒ `false`, `kbps = 0`.
     Use `CultureInfo.InvariantCulture`, `NumberStyles.AllowDecimalPoint`.
   - `bool MeetsMinimum(string? raw, int minimumKbps)` ⇒
     `TryParseKbps(raw, out var k) && k >= minimumKbps`. Unparseable ⇒ `false`
     (excluded under an active minimum — AC3).
3. New file
   `tests/StreamsPlayer.Core.Tests/StreamBitrateTests.cs`:
   - `TryParseKbps`: `"128"`→128, `"128 kbps"`→128, `"320k"`→320,
     `"1.5 Mbps"`→1500, `""`/`null`/`"high"`→false.
   - `MeetsMinimum`: `("128",128)`→true, `("96",128)`→false,
     `(null,128)`→false, `("garbage",1)`→false.
4. [CatalogMergerTests.cs](../../tests/StreamsPlayer.Core.Tests/CatalogMergerTests.cs):
   update the `Entry` factory positional call for the new record params, and add
   assertions in `Merge_UpdatesCatalogMetadataButPreservesLocalState` that
   `Protocol`/`Format`/`Bitrate`/`IsLive` are carried onto the merged catalog row
   while `Pinned`/`SortIndex`/`LastPlayOutcome` are still preserved (AC4).

## Static check

`dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~StreamBitrateTests|FullyQualifiedName~CatalogMergerTests"`
expected: build succeeds; bitrate + merger tests pass | actual: 16 passed, 0 failed
