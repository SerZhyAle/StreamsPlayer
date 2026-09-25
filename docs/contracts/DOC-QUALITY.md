# DOC-QUALITY - pointer

| | |
| --- | --- |
| **Id** | `DOC-INTERNAL-QUALITY` (adopted); `DOC-EXTERNAL-QUALITY` (not adopted - see below) |
| **Version** | 0.9 (draft) |
| **Home** | `Contracts/documentation-quality/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** (adopted 2026-09-26) |

## What this repository must do to stay conformant

1. **One home per document, roles declared.** `DOCS_SURFACES.md` is the ship-together manifest (source,
   render target, generator, gate per surface); `docs/README.md` indexes the hand-written tree. Render
   targets (`docs/*.html`, `docs/<code>/`, `sitemap.xml`) are regenerated, never hand-edited.
2. **Generated docs stay in sync.** `pwsh -NoProfile -File tools/site/build-site.ps1 -Check` fails when
   `docs/` drifts from its copy decks; `InstallTrustPageTests`, `LocalizationParityTests` and
   `LocalizedCallSiteTests` gate the localized surfaces in `dotnet test`.
3. **Open gaps are ticketed, not waived:** `SP-0140` - a machine-readable registry with reverse coverage, a
   cross-link/anchor/asset/house-style gate, and the site `-Check` wired into CI.

`DOC-EXTERNAL-QUALITY` is not adopted: this product publishes a product site (home, how-to, privacy, install
trust - governed by `PAGE-CONTENT`), not a task-oriented documentation corpus. Adopting it is the owner's
call, recorded as `SP-0140` open question 1.
