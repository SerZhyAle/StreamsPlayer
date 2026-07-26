# Phase 1 - Core contract + sidecar parser

Produces the platform-neutral half of the atlas contract. No imaging, no HTTP.

## Steps

1. **New `src/StreamsPlayer.Core/ChannelPreviewAtlas.cs`.**
   - `public readonly record struct PreviewTileRect(int Left, int Top, int Width, int Height)` - a plain
     Core type; `System.Windows.Int32Rect` lives in WindowsBase and must not reach Core.
   - `public static class ChannelPreviewAtlas` with `TileWidth = 240`, `TileHeight = 135`, `Columns = 34`.
     Comment states these mirror the offline packer (`delivery/stream-catalog/README.md` "Channel preview
     atlas") and that changing one side drifts every rect.
   - `RectFor(int index)` -> `left = index % Columns * TileWidth`, `top = index / Columns * TileHeight`.
   - `IsInBounds(int index, int sheetWidth, int sheetHeight)` -> `false` for a negative index, else the rect
     must fit inside the sheet. Guards a stale sidecar pointing past a shrunk sheet.
   - Static check: `dotnet build src/StreamsPlayer.Core -c Release` succeeds.

2. **New `src/StreamsPlayer.Core/ChannelPreviewCoords.cs`.**
   - `public static IReadOnlyDictionary<string, int> Parse(string json)` over `System.Text.Json`.
   - Accepts a flat object of `url -> index`. A number parses; a numeric string parses; anything else is
     **skipped defensively** so one malformed entry cannot poison the map (mirrors the Android store).
   - Blank/whitespace input and a non-object root return an empty map rather than throwing; malformed JSON
     throws `JsonException` for the caller to treat as "not installed".
   - Static check: build succeeds.

3. **New `tests/StreamsPlayer.Core.Tests/ChannelPreviewAtlasTests.cs`.**
   - `RectFor` maps the documented examples: index `0` -> `(0,0)`, `33` -> `(7920,0)`, `34` -> `(0,135)`,
     `68` -> `(0,270)`, and the live max `1880` -> `(2160,7425)`.
   - `IsInBounds` is true for `1880` against the shipped `8160 x 7560` sheet and false for `1904` (the first
     index past a full 56-row sheet) and for `-1`.
   - `Parse` reads a three-entry object, keeps the numeric-string form, skips a `null`/object/array value,
     and returns empty for `""`.
   - Static check: `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~ChannelPreviewAtlasTests"`
     - expected: all new tests pass.

## Stop conditions

- If `RectFor(1880)` does not land inside `8160 x 7560`, the published geometry differs from the README -
  stop and re-measure against `temp/SP-0031/atlas.webp` before writing any consumer.
