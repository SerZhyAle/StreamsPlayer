# SP-0018: Stream quality details and filtering

**Status:** Approved

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
