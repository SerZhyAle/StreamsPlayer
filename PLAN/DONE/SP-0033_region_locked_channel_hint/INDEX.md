# SP-0033 tactical plan: region-locked channel hint

Strategic ticket: [../SP-0033_region_locked_channel_hint.md](../SP-0033_region_locked_channel_hint.md)

## Topology

```
Phase 1 (Core contract)  produces: ChannelAccess, CatalogEntry.Access, StreamChannel.Access, CSV parse, merge
      |                  consumes: nothing
      v
Phase 2 (Listing badge)  produces: ChannelRow.RegionRestricted*, list + grid marker, localized strings
      |                  consumes: StreamChannel.Access
      v
Phase 3 (Failure hint)   produces: conditional region hint in PlaybackFailureDialog
      |                  consumes: StreamChannel.Access, localized strings from Phase 2
      v
Phase 4 (Verification)   consumes: everything above; produces run-and-observe evidence
```

Phases 2 and 3 both depend on Phase 1 only; they are ordered 2-then-3 because Phase 3 reuses the
localized string resource introduced in Phase 2. No phase may be skipped or reordered.

## Coverage map

| Strategic item | Phase |
|---|---|
| AC 1 - parsed, retained, persisted, survives restart and refresh | 1 |
| AC 2 - marker in list and grid, every tile size | 2 |
| AC 3 - untagged/empty/unrecognised visually unchanged | 1 (parse), 2 (render) |
| AC 4 - failure message explains region restriction | 3 |
| AC 5 - nothing extra on success | 3 |
| AC 6 - English, Russian, Ukrainian strings | 2, 3 |
| AC 7 - pre-change state file loads without migration | 1 |
| AC 8 - build/tests, parse+persist covered by tests, UI run-and-observe | 1, 4 |
| Decision 1 - two surfaces, informational only | 2, 3 |
| Decision 2 - only known values shown | 1 |
| Decision 3 - hedged wording | 2, 3 |
| Decision 4 - unobtrusive marker | 2 |
| Decision 5 - failure copy conditional | 3 |
| Decision 6 - localized, no emoji | 2, 3 |
| Constraint - refresh must not churn state | 1 |
| Constraint - pre-change channel loads without migration | 1 |
| Constraint - Core platform-neutral, presentation in App | 1 vs 2/3 split |
| Constraint - catalog-owned, only updated for catalog-origin rows | 1 |

## Phases

1. [PHASE-1_core_contract.md](PHASE-1_core_contract.md)
2. [PHASE-2_listing_badge.md](PHASE-2_listing_badge.md)
3. [PHASE-3_failure_hint.md](PHASE-3_failure_hint.md)
4. [PHASE-4_verification.md](PHASE-4_verification.md)
