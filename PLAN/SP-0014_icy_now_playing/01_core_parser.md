# Phase 01 — Core ICY StreamTitle parser

**Produces:** `StreamsPlayer.Core.IcyMetadataParser`
**Consumes:** — (style reference: `M3uPlaylistParser`, `StreamCatalogCsvParser`)

## Change

Create `src/StreamsPlayer.Core/IcyMetadataParser.cs`:

- `public static class IcyMetadataParser`.
- `public const int MaxTitleLength = 512;` — defensive UI/size bound (an ICY
  block is already ≤ 4080 bytes, but the app caps the surfaced title).
- `public static string? ExtractStreamTitle(string metadataBlock)`:
  1. Return `null` for null/empty/whitespace input.
  2. Locate `StreamTitle='` (ordinal). If absent, return `null`.
  3. Read the value up to the closing `';` (or end of block if the terminator is
     missing — tolerate malformed blocks).
  4. Sanitize: strip control characters (`char.IsControl`, keep normal spaces),
     collapse to a single trimmed line, and clamp to `MaxTitleLength`.
  5. Return `null` if the sanitized value is empty (a `StreamTitle='';` block =
     "no current title" → caller shows station only).

The block passed in is the already-decoded metadata text; byte decoding is the
reader's job (Phase 02). Keep the class pure and allocation-light; no I/O, no
`HttpClient`, no WPF.

## Rationale

Isolating extraction as a pure static function makes AC #4 (oversized, malformed,
empty metadata is safe) unit-testable without a network (Phase 03), and keeps the
untrusted-text bound inside platform-neutral Core.

## Static check

`dotnet build src/StreamsPlayer.Core -c Debug`
expected: build succeeds, 0 warnings from the new file | actual: Build succeeded, 0 Warning(s), 0 Error(s)
