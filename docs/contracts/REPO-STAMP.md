# REPO-STAMP - pointer

| | |
| --- | --- |
| **Id** | `REPO-STAMP` (with `HARNESS-PROFILE`, `REPO-LAYOUT`, `RULE-DELIVERY`) |
| **Version** | 0.9 (draft) |
| **Home** | `Contracts/rule-adoption/README.md` |
| **Owner** | sza-unified-rules |
| **This product's role** | **consumer and producer** |

## What this repository must do to stay conformant

1. **Repository Stamp:** Root `.sza-canon.json` is maintained with valid JSON, required keys (`canon`, `overlay`, `ledgerShape`), correct role (`product`), and verified core digest.
2. **Standard Repository Layout:** Maintain root `CLAUDE.md`, `AGENTS.md`, `README.md`, `LICENSE`, and local pointers in `docs/contracts/<ID>.md` indexed by `docs/contracts/README.md`.
3. **Canon Delivery Handshake:** Adopted plugin version and digest are kept fresh without hand-editing.

Evidence: `.sza-canon.json`, `AGENTS.md`, `CLAUDE.md`, `docs/contracts/README.md`.
