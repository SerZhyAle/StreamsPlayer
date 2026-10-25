# REPO-STAMP / HARNESS-PROFILE / REPO-LAYOUT / RULE-DELIVERY - pointer

| | |
| --- | --- |
| **Id** | `REPO-STAMP`, `HARNESS-PROFILE`, `REPO-LAYOUT`, `RULE-DELIVERY` |
| **Version** | 0.13 / 0.13 / 0.11 / 0.12, read 2026-10-25 (release 26.1025.1927) |
| **Home** | `Contracts/rule-adoption/README.md` |
| **Owner** | sza-unified-rules |
| **This product's role** | Consumer and producer of repository declarations |

The stamp is .sza-canon.json. This product keeps ledger shape 4, null ledgerFile, no dev-log writer
and no .sza-profile.json override. HARNESS-PROFILE is **not bound** here (rule 7): the shipped harness is
not run in this repository, and its defaults (ticket ids, archive folder, registry path) do not match
SP-NNNN, PLAN/DONE and the root DOCUMENT_REGISTRY.jsonl; the registry row reads `-` / `-`. The release
queue remains PLAN/RELEASE_QUEUE.md.
This sync does not edit canon rules, other products' declarations or the installed plugin.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
