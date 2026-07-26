# SP-0033: Surface region-locked channels from the catalog `access` tag

**Status:** Verified

Tactical plan: [SP-0033_region_locked_channel_hint/INDEX.md](SP-0033_region_locked_channel_hint/INDEX.md)

> Renumbered from SP-0031 on 2026-07-26: a concurrent session had claimed that id for the channel
> preview atlas (`PLAN/DONE/SP-0031_channel_preview_atlas.md`) while this ticket was in progress.
> The ticket, its plan folder, its code comments, and its `tmp/uia/sp0033-*` evidence were all moved.

## Implementation notes

- `src/StreamsPlayer.Core/Models.cs` - `ChannelAccess` enum (`Open` first), `CatalogEntry.Access`
  (trailing optional parameter), `StreamChannel.Access` (defaults `Open`).
- `src/StreamsPlayer.Core/StreamCatalogCsvParser.cs` - `ParseAccess`; only `geo` is recognised.
- `src/StreamsPlayer.Core/CatalogMerger.cs` - `Access` propagated to added and updated catalog rows.
- `src/StreamsPlayer.App/ChannelRow.cs` - `RegionRestrictedVisibility` / `Label` / `Tip`.
- `MainWindow.xaml` - amber pill on the list metadata row (in a `Grid` so the text keeps trimming) and
  on the grid tile (sharing a `StackPanel` with the pinned badge).
- `PlaybackFailureDialog.xaml`/`.xaml.cs` - conditional hint row; window switched to
  `SizeToContent="Height"` with `MinHeight="180"`. Both call sites pass `channel.Access`.
- `Localization.{en,ru,uk}.xaml` - `RegionRestrictedLabel`, `RegionRestrictedTip`,
  `FailureRegionRestricted` at full parity.

## Last Audit - 2026-07-26

Audited against the live working tree, not against the plan's claims. `./scripts/check.ps1`
expected: build clean, all tests pass | actual: 0 Warning(s), 0 Error(s); 220/220 passed.

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Parsed, retained, persisted, survives restart + refresh | PASS | `Parse_RecognisesOnlyTheDocumentedAccessValue`, `Merge_CarriesAccessOntoAddedAndUpdatedCatalogRows`, `Save_RoundTripsChannelAccessAndDefaultsLegacyStateToOpen` |
| 2 | Marker in list and grid, every tile size, displaces nothing | PASS | `sp0033-list-en.png`, `sp0033-grid-en.png`; tile content sits in a `Viewbox`, so size is not a variable |
| 3 | Untagged / empty / unrecognised visually unchanged | PASS | `paywall` and blank parse to `Open`; open + manual rows carry no pill in the UIA tree |
| 4 | Failure message explains region restriction | PASS | `sp0033-dialog-geo.png`; hint text present in dialog text nodes |
| 5 | Nothing extra on success | PASS | hint is set only in the failure dialog's constructor; `sp0033-dialog-open.png` identical to pre-change |
| 6 | English, Russian, Ukrainian | PASS | pill observed in all three; failure hint observed in en + ru; uk string present and on the same `DynamicResource` path |
| 7 | Pre-change state loads without migration | PASS | `sp0033-legacy.png` + the legacy-JSON half of the store test |
| 8 | Build/tests green, parse+persist tested, UI run-and-observe | PASS | above |

Contract checks: Core takes no WPF dependency (grep for `System.Windows` in Core: no matches); the
`SourceOrigin != Catalog` merge guard is untouched and covered by
`Merge_UserRowNeverTakesAccessFromTheCatalog`; no new network call, so explicit-refresh is unaffected.

One item was attempted and not observed, and it is **not** an acceptance criterion: hovering the pill to
see its tooltip could not be automated on this display (DPI mismatch between `SendInput` absolute
coordinates and UIA `BoundingRectangle` - detail in Phase 4). The binding and its localized string are
in place and the sibling label was observed in all three languages.

Note for whoever picks up the neighbouring work: `PLAN/DONE/SP-0031_channel_preview_atlas.md` sits in
`DONE/` at status `Implemented` with AC 4 and AC 6 unproven. Per `docs/agent/SPEC_LIFECYCLE.md` only a
`Verified` ticket belongs there. Not this ticket's scope; flagged, not touched.

## Goal

Consume the catalog's `access` column and tell the user that a channel is *region-restricted*
rather than broken: mark it in the catalog listing, and replace the bare failure message with an
explanation when such a channel does not play.

## Why

The upstream catalog contract gained an `access` column. Its only non-empty value today is `geo`:
the maintainer's deep-signal probe received HTTP 403/451, so the stream is *deliberately kept* in
the bank because it may well play for a user inside the right country. Roughly 42 of the shipped
rows carry it - national broadcasters such as CBS, Cubavision, DR1 and Puls 2.

StreamsPlayer currently ignores the column entirely. Those channels look identical to every other
one, and when they fail the user sees a generic playback error. That reads as "this app is broken"
for a channel the catalog knowingly ships as conditionally playable. The information needed to say
something better is already in the data we download and discard.

## Non-goals

- Do not hide, drop, remove, or deprioritise region-locked channels. The upstream decision is that
  they stay in the catalog because they may work for the user; StreamsPlayer must not second-guess it.
- Do not attempt to detect the user's region, geolocate, probe, or route around a restriction.
  No proxy, VPN hint, or alternative-host lookup.
- Do not add a filter or hide-control for region-locked channels in this ticket. If demand appears,
  that is a separate change against the existing catalog filter set.
- Do not treat the tag as authoritative. It is a heuristic captured from one network vantage point.
- Do not change the explicit-refresh contract or the MANUAL/IMPORTED merge protection. Manually added
  and imported channels have no `access` value and must render exactly as they do today.
- No new logging facade.

## Decisions

1. **Two surfaces.** A region-locked channel is marked in the catalog listing (list and grid), and
   its playback-failure message explains the likely cause. The marker is informational only - it
   never blocks or alters a launch attempt.
2. **Only known values are shown.** An empty, absent, or unrecognised `access` value renders exactly
   as today. Unknown future values are ignored rather than displayed raw, so a later upstream addition
   cannot leak a machine token into the UI.
3. **Hedged wording.** Copy states that the channel *may* be unavailable in the user's region and may
   still work - never that it is blocked. The tag is a heuristic and a 403 can also be a hotlink or
   IP block.
4. **The marker is unobtrusive.** It must not compete with the channel name, favicon, or pinned state,
   and must remain legible at every tile size.
5. **Failure copy is conditional.** The region-restricted explanation appears only when the channel
   carries the tag *and* playback actually failed. A geo-tagged channel that plays says nothing.
6. **Localized in all shipped languages** - English, Russian, and Ukrainian - with no emoji.

## Constraints

- The value travels the existing catalog path: parsed with the rest of the row, persisted with the
  channel, and survives a refresh. A refresh that re-reads an unchanged row must not churn state.
- A stored channel from before this change has no value and must load without migration or error.
- Catalog parsing stays platform-neutral; the presentation stays in the app.
- Adding the field must not break the existing merge behaviour: the value is catalog-owned data and
  only ever updated for catalog-origin rows.

## Acceptance criteria

1. The `access` value is parsed from the catalog CSV, retained on the channel, persisted, and still
   present after a restart and after a subsequent catalog refresh.
2. A channel tagged region-restricted shows an unobtrusive marker in both list and grid mode, at every
   tile size, without displacing the name, favicon, or pinned indicator.
3. A channel with no tag, an empty tag, or an unrecognised value is visually unchanged from today.
4. When a region-restricted channel fails to play, the failure message states that it may be
   unavailable in the user's region and may still work elsewhere - not a bare error.
5. When a region-restricted channel plays successfully, nothing extra is shown.
6. All new user-facing strings exist in English, Russian, and Ukrainian.
7. A state file written before this change loads without error and without a migration step.
8. Build and tests pass; parsing and persistence are covered by tests, and the two UI surfaces are
   confirmed by run-and-observe evidence recorded as `expected: ... | actual: ...`.

## Risks

- **False positives.** A 403 caused by hotlink protection or an IP block is tagged `geo` upstream, so
  a channel may be marked region-restricted while being simply dead. Hedged wording (Decision 3) is
  the mitigation; the marker must never be phrased as a verdict.
- **Visual noise.** ~42 tagged rows in a 2000+ row catalog is sparse, but a heavy marker would still
  degrade the listing. Keep it subordinate to existing tile elements.
- **Contract drift.** Upstream may add further `access` values. Decision 2 keeps an unknown value
  inert rather than displayed.

## Open questions

None. Surfacing was settled with the owner: marker in the listing plus a conditional failure message,
no filter in this ticket.
