# DOC-QUALITY - pointer

| | |
| --- | --- |
| **Id** | `DOC-INTERNAL-QUALITY` (adopted); `DOC-EXTERNAL-QUALITY` (not adopted - see below) |
| **Version** | 0.9 (draft); the external contract is 0.10 and not adopted |
| **Home** | `Contracts/documentation-quality/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** (adopted 2026-09-26, gated by `SP-0140`) |

## What this repository must do to stay conformant

1. **Every document is declared, both ways.** [`DOCUMENT_REGISTRY.jsonl`](../../DOCUMENT_REGISTRY.jsonl) lists
   every maintained document - every `*.md` and every generated `docs/**/*.html` - with topic, product
   area, update triggers and role; a `render` entry names its generator and is never hand-edited. A new
   document gets its entry in the same change, or an `ignore` record with a reason. `DOCS_SURFACES.md`
   stays the ship-together manifest (which surfaces move together); the registry is the per-file index.
2. **One gate judges the corpus.** `pwsh -NoProfile -File scripts/check-docs.ps1` fails on an undeclared
   or missing document, a broken relative link or heading anchor, a link into a path git does not track
   (the planning folder), a missing image, `...` or an en/em dash in prose, an `http://` reference, or an
   embedded script, iframe, remote font or remote image. Exit 0/1/2 under `CHECK-VERDICT`.
3. **Generated docs stay in sync.** `pwsh -NoProfile -File tools/site/build-site.ps1 -Check` fails when
   `docs/` drifts from its copy decks; `InstallTrustPageTests`, `LocalizationParityTests` and
   `LocalizedCallSiteTests` gate the localized surfaces in `dotnet test`.
4. **Both gates run in CI and in `scripts/check.ps1`**, the release-parity gate.

Not gated: English-only code comments and the use of `ё` - both stay review items.

`DOC-EXTERNAL-QUALITY` is not adopted: this product publishes a product site (home, how-to, privacy, install
trust - governed by `PAGE-CONTENT`), not a task-oriented documentation corpus. Adopting it is the owner's
call, recorded as `SP-0140` open question 1.
