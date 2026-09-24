# Documentation surfaces

The ship-together manifest (canon `DOCUMENTATION_CONCEPT.md` §5): every surface that tells a person what
STREAMS Player does, where its text comes from, what regenerates it, and which languages it ships in.
A user-visible change is not done until every row it touches says the same thing, in every listed
language, in one edit. `scripts/release.ps1` points here instead of keeping a list of its own.

**Render targets are never edited by hand.** Edit the source column, run the generator, commit both.

## User-facing

| Surface | Source of truth | Generator / render target | Languages | Gate |
| --- | --- | --- | --- | --- |
| README | `README.md`, `README.ru.md`, `README.uk.md` (hand-written; `README.md` also ships in the release ZIP) | - | en, ru, uk | none |
| Product site - home, how-to, privacy, install trust | `tools/site/copy/<code>.txt`, `tools/site/templates/*.html` | `tools/site/build-site.ps1` -> `docs/index.html`, `docs/privacy.html`, `docs/trust.html`, `docs/<code>/*.html`, `docs/site.js`, `sitemap.xml`, `robots.txt` | 13 (en, ru, uk by hand; ten machine-translated, marked on the page) | `build-site.ps1 -Check` (keys and placeholders, not meaning) |
| Site lead screenshot | `assets/store/app-<listing-code>.png` | `tools/site/export-site-screenshots.ps1` -> `docs/assets/screens/grid-<code>.jpg` | 13 | none - **commit the JPEGs with the HTML that references them** |
| Site social card | - | `tools/site/make-og-image.ps1` -> `docs/assets/og-card.png` | 1 | none |
| Privacy statement | `privacy-network`, `privacy-local` keys of the site decks | as the site | 13 | must match the Store data-safety answers below and the code |
| Install trust page (`INSTALL-TRUST`) | `trust-*` keys of the site decks, `tools/site/templates/trust.html` | as the site; linked from the three READMEs and the site's Distribution section | 13 | `InstallTrustPageTests` (section order, quoted dialogs, no protection-weakening instruction); `trust-never-*` must say what the privacy statement says |
| Store listing copy | `msix/listing/<listing-code>.txt`, `shared.txt`, `search-terms*.txt`, `forbidden-terms.txt` | `tools/store/build-store-listing-csv.ps1` -> `msix/dist/store-listing-import.csv` | 13 (en-us, ru, uk by hand) | builder: forbidden terms, required fields, round trip |
| Store "What's new" | `msix/listing/release-notes/<version>.{en-us,ru,uk}.txt` and the blocks in `msix/store-listing.md` ("What's new") | `tools/store/write-release-notes.ps1` | en, ru, uk (the other ten get English) | 1,500 characters per block |
| Store data-safety, certification notes, runFullTrust text | `msix/store-listing.md` ("Certification notes", "runFullTrust justification", "Privacy and age-rating declarations") | pasted in Partner Center | en | must match the privacy statement |
| Store screenshots | the running app | `tools/store/capture-store-screenshots.ps1` -> `assets/store/app-<listing-code>.png` | 13 | UI Automation language check inside the script |
| Store composed images | - | `tools/store/make-store-images.ps1` -> `assets/store/screenshot-*`, poster, box art | en, ru | none |
| Third-party notices | `THIRD-PARTY-NOTICES.txt` (ships in the ZIP, the installer and the MSIX) | - | en | none |
| GitHub Release body | the commit messages since the previous tag | `.github/workflows/release.yml` (`generate_release_notes`) | en | none - write commit messages as user-facing notes |
| winget manifest | `winget/templates/` | copied per release | en | `winget validate` |
| In-app strings | `src/StreamsPlayer.App/Localization.<code>.xaml`; language list in `InterfaceLanguages` (Core) | - | 13 | `LocalizationParityTests`, `LocalizedCallSiteTests` |
| sza.od.ua hub card | outside this repository | - | en, ru, uk | touched only when the one-line answer to "what is this product" changes |

## Maintainer-facing, where they state product behaviour

| Surface | Covers |
| --- | --- |
| `AGENTS.md`, `CLAUDE.md`, `docs/agent/VALIDATION.md` | architecture, media engines, local state, the validation ladder |
| `scripts/smoke-playback.ps1` (header and round labels) | which media path each round plays through |
| `docs/PLAYBACK_RESILIENCE.md`, `docs/stream-playback-recommendations.md` | recovery rules, engine options, where FFmpeg is downloaded |
| `STORE_PUBLISHING.md`, `msix/README.md`, `msix/listing/README.md`, `winget/README.md` | channel procedures and what each screenshot shows |
| `docs/localization/glossary.md` | the one rendering of each recurring term, per language |
| `docs/contracts/` | pointers to the shared contracts this product consumes or owns |

## Ukrainian and Russian product name

The localized product name is «Трансляції» / «Трансляции» on every Ukrainian and Russian surface that
names the product in prose (owner decision, SP-0112). The site's brand mark, `og:site_name` and JSON-LD
keep `STREAMS Player`, the same in every language.

## Publishing

Every row above is local work until the owner asks for the one-way step: a push that touches `docs/`
deploys Pages, the Partner Center import and submission, the winget PR, and the hub card are each
ask-first. The site and the Store listing describe the working tree; they go out with the release that
carries what they describe, never before it.
