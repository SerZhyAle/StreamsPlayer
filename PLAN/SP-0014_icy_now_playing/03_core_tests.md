# Phase 03 — Core unit tests for the parser

**Produces:** `tests/StreamsPlayer.Core.Tests/IcyMetadataParserTests.cs`
**Consumes:** Phase 01 (`IcyMetadataParser`)

## Change

Add `IcyMetadataParserTests` (xUnit, matching the existing test style in
`tests/StreamsPlayer.Core.Tests`). Cover the AC #4 / AC #6 surface:

- Well-formed: `StreamTitle='Artist - Song';StreamUrl='http://x';`
  → `"Artist - Song"` (trailing fields ignored).
- Empty title: `StreamTitle='';` → `null` (station-only presentation).
- Missing field: `StreamUrl='http://x';` → `null`.
- Whitespace-only input and empty input → `null`.
- Oversized title (> `MaxTitleLength`) → clamped to `MaxTitleLength`.
- Control characters (`\0`, `\n`, `\t` inside the value) stripped/collapsed.
- Malformed / unterminated: `StreamTitle='Artist - Song` (no closing `';`)
  → best-effort value, no exception.

## Static check

`dotnet test tests/StreamsPlayer.Core.Tests -c Debug --filter "FullyQualifiedName~IcyMetadataParserTests"`
expected: all new tests pass | actual: Passed 10/10 (`IcyMetadataParserTests`).

## Extension (in-spirit): reader integration tests

Added `tests/StreamsPlayer.Core.Tests/IcyMetadataReaderTests.cs` — a deterministic
loopback `TcpListener` speaking the ICY protocol (ephemeral port, no external
network). Covers: extracts + reports changed `StreamTitle` from a real HTTP+stream
exchange; cleanly reports nothing when `icy-metaint` is absent (AC #2). This
strengthens AC #1/#4 runtime evidence beyond the pure parser.
expected: reader tests pass | actual: Passed (2/2). Full ICY filter: 12/12.
