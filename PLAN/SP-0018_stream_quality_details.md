# SP-0018: Stream quality details and filtering

**Status:** Implemented (manual GUI observation pending — BlockNeedUserTest)

Tactical plan: [SP-0018_stream_quality_details/INDEX.md](SP-0018_stream_quality_details/INDEX.md)

## Verification

Automated evidence (all green):
- `./scripts/check.ps1` — Release build + **126/126** tests pass (adds parser, `StreamBitrate`,
  and merger carry-through coverage — AC6 parsing/filter tests).
- Smoke launch loaded the real 2182-channel state with no exception; the new ComboBox active
  indicator and the 4th card row parse at runtime.
- Live-bank check: `streams.csv` header carries `protocol/format/bitrate/is_live`; bitrate is
  populated as bare integer kbps (64/96/128/192/256) for 348/400 sampled rows, 52 blank —
  matching the parser, the `StreamBitrate` thresholds, and the AC3 default-visible/blank case.

Pending (needs a human glance, after **Update catalog** repopulates the new fields): the visual
run-and-observe of the details line, the min-bitrate filter combining with search/facets, the
active indicator, and the RU/EN relabel. See
[05_validation.md](SP-0018_stream_quality_details/05_validation.md).

AC5 (no playback decision or success mark from metadata) held by construction: no playback,
status-bullet, or `LastPlayOutcome` path was touched.

## Goal

Expose available catalog format, bitrate, protocol, and live-status metadata as compact technical details and let users narrow the catalog by known bitrate.

## Why

Users may prefer a lower-bandwidth radio stream on constrained connections or a higher-bitrate stream when quality matters. The bank already supplies optional maintainer metadata that can support this decision.

## Non-goals

- Measure real-time bandwidth or certify audio/video quality.
- Probe every catalog URL in the background.
- Reject or hide channels merely because technical metadata is absent.
- Change the CSV delivery contract.

## Constraints

- Technical fields remain optional, untrusted catalog claims and are labelled accordingly.
- The default catalog view includes rows with known and unknown quality exactly as before.
- Activating a minimum-bitrate filter excludes rows whose bitrate is missing or cannot be interpreted and visibly indicates that the filter is active.
- Format, protocol, bitrate, and live status are displayed only when present; unknown values use one quiet fallback rather than an error.
- Catalog-declared media kind continues to win over URL classification.

## Acceptance criteria

1. A channel with valid technical metadata can show compact format, bitrate, protocol, and live-status details without crowding the default card.
2. Users can activate and clear a minimum-bitrate filter, and the result combines predictably with existing search and facets.
3. Unknown or malformed bitrate values remain visible under the default filter and are excluded under an active minimum.
4. Refresh updates technical metadata for catalog rows while preserving all existing user-owned and ordering state.
5. No playback decision or success mark is inferred solely from advertised metadata.
6. Parsing/filter tests and a localized run-and-observe check pass.

## Risks

Catalog metadata can be stale, inconsistent, or unsuitable for comparing different media kinds. Presentation must avoid implying measured quality or guaranteed bandwidth.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md) and optional CSV fields in [streams specification](../docs/specifications/streams.txt).
