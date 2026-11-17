# Contract catch-up, 2026-11-15

This is the product's evidence record for SP-0278: bring StreamsPlayer (Windows) to every shared contract that moved since the
version it was last synchronized with, and make the same check the first step of every release. It reads each log entry and
amendment since the registry's synchronized version against the working tree of 2026-11-15. **Nothing was built, run or observed**:
every verdict below is a reading, and a reading is not a proof. Contract homes are resolved through
[the agent-rules pointer](../../CLAUDE.md); no contract is copied here. The Android products' rows were not touched.

Method: the live spec was generated with the canon's `contract-lag.ps1 -Product StreamsPlayer -EmitSpec` (exit 0; across every surface 4 behind,
29 partial, 2 pending and 12 unparsed, of which the Windows product's share was 2, 25, 0 and 8), five read-only readers took the contracts in
five groups, and every fact that changed a registry row was re-checked in the tree before it was written (`verifiers` in the
announcement, the link-password choice, the missing `forward` and `sensitive` members, the missing `noindex`, the mixed Settings
row, the unstyled disclosure, the catalog's `action.unhide` record). No product source was edited, so there is nothing to compile.

## Result

| Measure | Before | After |
| --- | --- | --- |
| Windows rows `behind` | 2 (`BUTTONS`, `SETTINGS-NAVIGATION`) | 0 (both read at 2.2 and 2.3, now current) |
| Windows rows `pending` | 0 | 0 |
| Windows rows `unparsed` | 8 (`APP-SETTINGS`, `INPUT-PARITY`, `PAGE-CONTENT`, `PAGE-STYLE`, `SITE-FAMILY-MAP`, `STREAM-BANK`, `WAVE-PARTICLES`, `WINDOWS-UI`) | 0 (the partial claim moved to `Implements`) |
| Windows rows `partial` | 25 | 33, each at the current version, verified 2026-11-15, with a dated note and, where a gap exists, a dated exception |

The `partial` state stays: it records that no contract of the group is claimed whole. What the pass moved is the version each
row names, the date, and the reason for every remaining gap. The registry block "StreamsPlayer contract catch-up - 2026-11-15"
holds 15 new dated exceptions and 8 closed or narrowed rows; remediation tickets are SP-0279 to SP-0282.

## Per-contract outcome

| Contract (read at) | Outcome | Remainder |
| --- | --- | --- |
| APP-ACTIVATION 0.15 | Holds by reading (attach way plus a sender assist, bounded sends, exit code of a command) | Hung receiver, other session, C3 count |
| APP-BEHAVIOUR 0.14, APP-STYLE 0.15 (owned here) | Holds; one borderline of APP-STYLE item A (state dot, 12 px badge) | Recommendation to the owner of both, no amendment written; live theme run |
| APP-COMMANDS 0.2, CAPTURE-OUTPUT 0.8 | Items A, N, R and the gate of F hold; gaps C, E, F, H, I, J, K, L, M and the folder rule | SP-0279; reading questions filed |
| DIAGNOSTIC-REPORT 0.15 (owned here) | Holds (open reason set, `<USER>` placeholders, vacuous `unavailable`) | Vectors not vendored |
| BOOLEAN-OPTION 2.2, COLLAPSIBLE-GROUP 1.3, ICON-RENDER 0.25, ICON-SET 0.41 | One mixed glyph row, five stock disclosures, two action-glyph headers, a stale vendored map | SP-0280 |
| BUTTONS 2.2, SETTINGS-NAVIGATION 2.3, UI-SIZING 1.2, WINDOWS-UI 0.12, APP-SETTINGS 0.8 | Wear OS, web and Android items out of scope; 2.1 A broadcast source choices; the 2.0 gaps stand under SP-0231 | SP-0280, SP-0231 |
| WAVE-PARTICLES 0.16 | Rule 9 holds; the 2026-10-26 exception closes | High-contrast reading |
| CONTEXT-HELP 0.4 | F1, `inherit`, item K hold on Main, First-run and Broadcast; G (`noindex`) diverges | SP-0282; Settings anchor question filed |
| LIVE-BROADCAST 1.8, DEVICE-EXCHANGE 1.15, BROADCAST-RESOURCE 1.1 | R, T, U, X, DV, DS, DT, EG hold; V, W, FB, DY, CW, ED diverge; the 2026-10-22 R/DV rows close | SP-0281 |
| STREAM-BANK 2.10 | Merge, retirement, parser and pairing hold; rule 14 narrows to an optional fold; 2.6, 2.8, 2.10 are a question | SP-0281; scope question filed |
| CHECK-VERDICT 0.14, CHECK-BASELINE 0.12, BUILD-EVIDENCE 0.11 | A.3 and A.4 hold; A.1 and item B diverge as their rows say | SP-0282; token form question filed |
| DOC-EXTERNAL-QUALITY 0.14, DOC-INTERNAL-QUALITY 0.13 | T, U, J hold; O and W diverge | SP-0282 |
| SITE-EXPERIENCE 0.6, SITE-STRUCTURE 0.4, SITE-REPRESENTATION 0.2, PAGE-CONTENT 1.5, PAGE-STYLE 3.10, SITE-FAMILY-MAP 1.12 | Rule 13 holds; `?lang=ua` ignored; `docs/` behind `tools/site/copy` | SP-0282; redirect question filed |
| WINDOWS-STORE 0.5 (owned here), PACKAGE-VERSIONING 0.8 | Bare image form holds in code; rule 8 output refusal missing; 0.6 to 0.8 touch only Android bands | SP-0282 |
| INPUT-PARITY 0.7 | Player key set holds by reading; exception text was stale | Run-and-observe; rotate row question |

## Questions filed in the catalog (proposals, nothing amended)

`app-commands/PROPOSAL-2026-11-15-streamsplayer-reading-questions.md`, `stream-catalog/PROPOSAL-2026-11-15-streamsplayer-stream-bank-06-scope.md`,
`in-app-help/PROPOSAL-2026-11-15-streamsplayer-help-button-settings-anchor.md`, `automated-checks/PROPOSAL-2026-11-15-streamsplayer-bare-verdict-tokens.md`,
`product-web-pages/PROPOSAL-2026-11-15-streamsplayer-landing-language-redirect.md`, `iconography/PROPOSAL-2026-11-15-streamsplayer-state-overlay-sibling-set.md`.

## Not run, and what it would take (RELEASE items)

- `scripts/check.ps1` (Release build, the test suite, the documentation gate, the site check): the release's contract gate runs it; the
  site check stays red until `docs/` is regenerated for the pending decks and the `Windows media and file manager` label (SP-0282).
- `scripts/smoke-playback.ps1`, any F1 or button walk, `Sync-IconGlyphs.ps1 -Check`, `IconographyConformanceTests`, `CatalogHarness`,
  a live exchange server for DU and the producer rows, a Partner Center re-export for the bare image form, a browser for the site items,
  the Light, Dark and high-contrast run of the new windows. Each is recorded as a `RELEASE` item, not asked about.
- The vendored vectors were compared with the catalog by `cmp` (byte-identical) but not replayed in this pass.

## Release step (CONTRACTS section 6 item 6)

Before a version is cut, repeat the catch-up for the contracts that moved since the last release: generate the spec with
`contract-lag.ps1 -Product StreamsPlayer -EmitSpec`, read each new log entry against the tree, then move the product's own rows and
their exceptions. A release is not cut on a row that is stale or behind without a dated reason.
