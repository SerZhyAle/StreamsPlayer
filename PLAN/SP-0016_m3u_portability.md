# SP-0016: M3U import and export portability

**Status:** Approved

## Goal

Let users bring compatible M3U channel lists into StreamPlayer and export their own curated channels or collections without requiring an account.

## Why

M3U is a practical interchange format across stream players. Portable lists protect user effort and make StreamPlayer easier to adopt while preserving its local-first model.

## Non-goals

- Download media for offline use.
- Treat an HLS media manifest as a list of channels.
- Export the full third-party catalog or redistribute its metadata.
- Synchronize lists automatically between devices.

## Constraints

- Import accepts a user-selected local `.m3u`/`.m3u8` file or an explicit HTTP(S) playlist URL.
- The existing playlist parsing, URL validation, provenance, URL de-duplication, and user-row-wins contracts remain authoritative.
- An input containing `#EXT-X-` is treated as an HLS manifest and imports zero channels with a clear explanation.
- Import is atomic and previews counts for new, duplicate, invalid, and skipped entries before applying changes.
- Export is limited to `MANUAL`/`IMPORTED` rows chosen directly, through favorites, or through one named collection; catalog-only rows are not exported.
- Exported credential-bearing URLs require an explicit warning before they are written in clear text.

## Acceptance criteria

1. Valid local and remote M3U lists import channel titles and launchable URLs with `IMPORTED` provenance.
2. Duplicate URLs do not create duplicate rows or overwrite existing `MANUAL`/`IMPORTED` data.
3. HLS manifests, inaccessible sources, invalid encoding, invalid URLs, and empty lists leave current state unchanged and explain the outcome.
4. Before applying a valid import, the user sees accurate category counts and can cancel without changing state.
5. The user can export selected user-owned channels, favorites, or a named collection to a valid UTF-8 M3U file and re-import it without losing titles or order.
6. Import/export contract tests and a Windows file-picker run-and-observe check pass.

## Risks

M3U files have inconsistent encodings and informal extensions. Export can expose embedded credentials, so the application must warn rather than silently leaking them.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md) and the playlist rules in [streams specification](../docs/specifications/streams.txt).
