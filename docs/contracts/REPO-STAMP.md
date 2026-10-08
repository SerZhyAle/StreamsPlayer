# REPO-STAMP - pointer

| | |
| --- | --- |
| **Id** | `REPO-STAMP` (with `HARNESS-PROFILE`, `REPO-LAYOUT`, `RULE-DELIVERY`) |
| **Version** | 0.12 / 0.11 / 0.11 / 0.12 (draft) |
| **Home** | `Contracts/rule-adoption/README.md` |
| **Owner** | sza-unified-rules |
| **This product's role** | **consumer and producer** |

## What this repository must do to stay conformant

1. **Repository Stamp:** Root `.sza-canon.json` is maintained with valid JSON, required keys (`canon`, `overlay`, `ledgerShape`), correct role (`product`), and verified core digest.
2. **Standard Repository Layout:** Maintain root `CLAUDE.md`, `AGENTS.md`, `README.md`, `LICENSE`, and local pointers in `docs/contracts/<ID>.md` indexed by `docs/contracts/README.md`.
3. **Canon Delivery Handshake:** Adopted plugin version and digest are kept fresh without hand-editing.

Evidence: `.sza-canon.json`, `AGENTS.md`, `CLAUDE.md`, `docs/contracts/README.md`.

**Read 2026-10-07 (SP-0206).** `REPO-STAMP` 0.12 rule 4 and `RULE-DELIVERY` 0.12 rule 5 (an unknown stamp age reads as past the
180-day window): `.sza-canon.json` carries a parseable `adoptedOn` and `reconciledOn`, so the reader change moves nothing here.
`HARNESS-PROFILE` 0.11 rule 2: this repository runs `ledgerShape` 4 with no `ledgerFile` and has no `.sza-profile.json`, so the
derived `paths.changelog` is empty - which is true: it keeps no dev-log. It does not run the shipped dev-log tools.
`REPO-LAYOUT` 0.11 rule 3: `PLAN/RELEASE_QUEUE.md` is the release queue under its default name.
