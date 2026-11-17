# DOC-INTERNAL-QUALITY / DOC-EXTERNAL-QUALITY - pointer

| | |
| --- | --- |
| **Id** | `DOC-INTERNAL-QUALITY`, `DOC-EXTERNAL-QUALITY` |
| **Version** | 0.13 / 0.14, 2026-11-15 (SP-0278) |
| **Home** | `Contracts/documentation-quality/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Internal consumer; scoped external help/portal contributor |

`DOCUMENT_REGISTRY.jsonl` at the root and `scripts/check-docs.ps1` hold tracked source/render
coverage, links, assets and style. The root location is a permanent recorded variant of the new
layout wording; no ignored PLAN/temp inventory is published and no dev/ corpus is maintained.
`tools/site/build-site.ps1` and the help generator judge their render targets. Since SP-0282 each of the
nine `render` records names its `check` beside its `generator`, and `check-docs.ps1` refuses a render with no
check or one whose script is not a file of the tree (item W); the stamp names `docRegistryFile`. Since SP-0287
the registry declares the roots of the maintained corpus (item V) in `root` records beside its `ignore` records, each
with a reason: a document under a root is recorded or excluded by a reasoned ignore, and a file under no root is outside
the corpus, named by the gate and not judged. The help pages carry `noindex` (item O of the external contract).
External scope now
includes authored help and EN/RU/UK portal contributions; SP-0230/SP-0239 retain screenshot,
freshness, glossary/index and applicable SEO work. The shared portal owner publishes its corpus.
Both adoptions are partial; the former blanket external non-adoption claim is withdrawn.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
