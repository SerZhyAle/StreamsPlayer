# DOC-INTERNAL-QUALITY / DOC-EXTERNAL-QUALITY - pointer

| | |
| --- | --- |
| **Id** | `DOC-INTERNAL-QUALITY`, `DOC-EXTERNAL-QUALITY` |
| **Version** | 0.10 / 0.12, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/documentation-quality/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Internal consumer; scoped external help/portal contributor |

`DOCUMENT_REGISTRY.jsonl` at the root and `scripts/check-docs.ps1` hold tracked source/render
coverage, links, assets and style. The root location is a permanent recorded variant of the new
layout wording; no ignored PLAN/temp inventory is published and no dev/ corpus is maintained.
`tools/site/build-site.ps1` and the help generator judge their render targets. External scope now
includes authored help and EN/RU/UK portal contributions; SP-0230/SP-0239 retain screenshot,
freshness, glossary/index and applicable SEO work. The shared portal owner publishes its corpus.
Both adoptions are partial; the former blanket external non-adoption claim is withdrawn.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
