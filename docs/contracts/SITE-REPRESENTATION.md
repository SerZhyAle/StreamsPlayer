# SITE-REPRESENTATION - pointer

| | |
| --- | --- |
| **Id** | `SITE-REPRESENTATION` |
| **Version** | 0.1 (draft) |
| **Home** | `Contracts/product-site/SITE-REPRESENTATION.md`, with the run-list `SITE-CHECKLIST.md` beside it |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer at the page tier**, opted in 2026-10-06 (SP-0192) |

## Why it applies here

Rules 1 to 5 and 12 bind a page-tier site. The function-page rules (6 to 10) and the showcase rule (11) do not apply:
there are no function pages, no settings reference and no showcase.

## Where this repository meets it

- **Positioning source** (rules 1, 2): `POSITIONING.md` at the repository root - what the product is and three ordered
  pillars (internet radio, live video, RTSP), each with its term in English, Russian and Ukrainian. The surfaces it
  binds are named in `tools/site/site-facts.json`: the README and the Store listing in en, ru and uk, the site's lead
  sentence in en, ru and uk, and the site's key line in all thirteen locales. `build-site.ps1 -Check` reads the source
  and fails when a surface first names the pillars out of order or lacks one; the ten machine-translated locales are
  held to the pillar count and to the term that is not translated (RTSP). No store-policy exception is declared. Not
  judged: the other ten Store listing locales, and the wording around a pillar (SP-0198).
- **Facts typed once** (rule 3): the language count is the placeholder `[[languages]]`, rendered from `InterfaceLanguages`,
  and the minimum Windows is `[[windows]]`, rendered from `SupportedOSPlatformVersion` of the App project - no copy deck
  types either, and `build-site.ps1 -Check` fails a deck that does. The MSIX manifest and the winget manifest are compared
  with that minimum. The four channels are declared in `tools/site/site-facts.json` with the packaging file each one
  stands on; the deck has exactly one card for each, and the prose that lists the install routes names the two that keep
  their name in every language. Not judged: a language count typed in prose by the README or the Store listing, which
  are other surfaces (SP-0198).
- **Public editions** (rule 4): one edition, Windows x64, in four channels (setup executable, portable ZIP, Microsoft
  Store, winget), each under one name in the site copy.
- **Trust pages say one thing** (rule 12): the privacy, trust and store-data copy are changed in the same edit
  (`INSTALL-TRUST` pointer); `InstallTrustPageTests` pins the trust half.

## What this repository must do to stay conformant

1. Change privacy, trust and store-data text together.
2. State a limit next to what it limits (the unsigned installer warning stands beside the download).
3. Cite this contract by id and section, never by catalog path.

Evidence: `POSITIONING.md`, `tools/site/site-facts.json`, `tools/site/SiteFacts.ps1` with its fixture tests `tools/site/Test-SiteFacts.ps1`, `tools/site/copy/en.txt`, `DOCS_SURFACES.md`, `tests/StreamsPlayer.Core.Tests/InstallTrustPageTests.cs`, the registry rows and `PLAN/SP-0192`.
