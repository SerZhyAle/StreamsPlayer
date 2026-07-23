# Phase 01 — Core model + CSV parse

**Produces:** `Protocol`, `Format`, `Bitrate` (raw string), `IsLive` (bool?) on
`CatalogEntry` and `StreamChannel`, populated by the CSV parser.
**Consumes:** nothing.

## Steps

1. [Models.cs](../../src/StreamsPlayer.Core/Models.cs): add to `CatalogEntry`
   record parameters: `string? Protocol`, `string? Format`, `string? Bitrate`,
   `bool? IsLive`. Append them after `FaviconIndex` to minimise positional churn
   (record is constructed by name in the parser; positional in tests — update
   both in this phase and Phase 02).
2. [Models.cs](../../src/StreamsPlayer.Core/Models.cs): add to `StreamChannel`
   record the same four `init` properties: `public string? Protocol`,
   `public string? Format`, `public string? Bitrate`, `public bool? IsLive`.
   All nullable ⇒ older `catalog-state.json` deserializes them to `null`; no
   `SchemaVersion` change.
3. [StreamCatalogCsvParser.cs](../../src/StreamsPlayer.Core/StreamCatalogCsvParser.cs):
   in the `CatalogEntry` construction, pass `Optional("protocol")`,
   `Optional("format")`, `Optional("bitrate")`, and a parsed `is_live` value.
   For `is_live` use a local tolerant parse returning `bool?`:
   `true`/`1`/`yes`/`live` ⇒ `true`; `false`/`0`/`no`/`vod` ⇒ `false`; anything
   else (incl. blank/absent) ⇒ `null`. Case-insensitive, trimmed.
4. [StreamCatalogCsvParserTests.cs](../../tests/StreamsPlayer.Core.Tests/StreamCatalogCsvParserTests.cs):
   add a test that a row with `protocol,format,bitrate,is_live` populates the
   four fields, and that missing columns / blank cells leave them `null`.

## Notes

- Bitrate is **not** parsed to a number here — stored verbatim. Numeric
  interpretation is Phase 02's `StreamBitrate`.
- Do not change `FromCatalogValue`; catalog media-kind precedence is untouched.

## Static check

`dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~StreamCatalogCsvParserTests"`
expected: build succeeds and all parser tests pass | actual: 10 passed, 0 failed
