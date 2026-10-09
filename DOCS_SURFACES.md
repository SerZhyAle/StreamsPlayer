# Documentation surfaces

The ship-together manifest (canon `DOCUMENTATION_CONCEPT.md` §5): every surface that tells a person what
STREAMS Player does, where its text comes from, what regenerates it, and which languages it ships in.
A user-visible change is not done until every row it touches says the same thing, in every listed
language, in one edit. `scripts/release.ps1` points here instead of keeping a list of its own.

**Render targets are never edited by hand.** Edit the source column, run the generator, commit both.

## User-facing

| Surface | Source of truth | Generator / render target | Languages | Gate |
| --- | --- | --- | --- | --- |
| Positioning - the source every external surface below is written from | `POSITIONING.md` (hand-written: what the product is, the ordered pillars, each pillar's term in en, ru and uk) | - | en, ru, uk terms | `build-site.ps1 -Check` reads it through `tools/site/SiteFacts.ps1`; the surfaces it binds are listed in `tools/site/site-facts.json` (`SITE-REPRESENTATION` rules 1 and 2) |
| README | `README.md`, `README.ru.md`, `README.uk.md` (hand-written; `README.md` also ships in the release ZIP) | - | en, ru, uk | the pillars are first named in the order of `POSITIONING.md` (`build-site.ps1 -Check`) |
| Product site - home, how-to, privacy, install trust, what's new, support, not-found | `tools/site/copy/<code>.txt`, `tools/site/copy/notfound/<code>.txt` (en, ru, uk), `tools/site/templates/*.html`; the facts it states are declared in `tools/site/site-facts.json` | `tools/site/build-site.ps1` -> `docs/index.html`, `docs/privacy.html`, `docs/trust.html`, `docs/whats-new.html`, `docs/support.html`, `docs/404.html`, `docs/<code>/*.html`, `docs/site.js`, `sitemap.xml`, `robots.txt` | 13 (en, ru, uk by hand; ten machine-translated, marked on the page) | `build-site.ps1 -Check` (the language count and the minimum Windows are placeholders rendered from `InterfaceLanguages` and the App project and never typed in a deck, the MSIX and winget minimums agree with the project's, the channel cards and the prose that lists the install routes match the declared channels, and the key line lists the pillars of `POSITIONING.md` in all 13 locales - `SITE-REPRESENTATION` rules 1 to 3, `tools/site/Test-SiteFacts.ps1` holds the gate itself; keys and placeholders, not meaning; the vendored kit `docs/assets/sza-kit.css` and backdrop script `docs/assets/wave-particles.js` byte-identical to the pins in `tools/site/kit-provenance.txt` - `SITE-EXPERIENCE` rule 1, `WAVE-PARTICLES` section 7; every address in `tools/site/held-addresses.json` resolves against the generated pages, and no tracked file holds an unlisted one - `SITE-STRUCTURE` rule 8; `tools/site/Test-SiteDiscovery.ps1` also reads the generated pages themselves, structure only: every page and `404.html` links What's new and Support from its footer, the sitemap lists exactly languages x pages, and every page carries one structured-data block that parses, the not-found page none) |
| Site lead screenshot | `assets/store/app-<listing-code>.png` | `tools/site/export-site-screenshots.ps1` -> `docs/assets/screens/grid-<code>.jpg` | 13 | none - **commit the JPEGs with the HTML that references them** |
| Site social card | - (one artwork drawn by one function; the layout derives from the canvas height) | `tools/site/make-og-image.ps1` -> `docs/assets/og-card.png` (1200x630, the Open Graph and Twitter tags) and `docs/assets/social-preview.png` (1280x640, the repository social preview) | 1 | `make-og-image.ps1 -Check` (exact pixel size read from the PNG IHDR chunk of both files, each under 1 MiB) |
| Demo capture - the silent product recording on the site's landing and under the README tagline | the master recording `temp/SP-0039/demo-master.mp4` (not tracked: `temp/` is ignored; recorded again when the product's look changes) | `tools/site/make-demo.ps1` -> `docs/assets/demo/streamsplayer-demo.mp4` (the site `<video>`), `streamsplayer-demo.gif` (the README, the right-to-left segment of the master) and `streamsplayer-demo-poster.jpg` | 1 (no audio, no captions; the recording shows the English and the Arabic interface) | `make-demo.ps1 -Check`, offline and without ffmpeg: the MP4 box tree is read in PowerShell (duration under the limit, every track `vide` and none `soun`, `moov` before `mdat`), the GIF and poster signatures and ends, the byte caps, all three files present; exit 3 means a file of the set is not there; `scripts/check.ps1` runs it once `docs/assets/demo` holds a file |
| GitHub repository About (description, homepage, topics, social preview) | `POSITIONING.md` section 1 and the ordered pillars, the `InterfaceLanguages` count, the site address, the social card above | `gh repo edit` sets the description, homepage and topics; the repository Settings page takes the upload of `docs/assets/social-preview.png` (no API sets it) | en | read back with `gh repo view SerZhyAle/StreamsPlayer --json description,homepageUrl,repositoryTopics,usesCustomOpenGraphImage`: the description names the three pillars in the order of `POSITIONING.md` and holds the `InterfaceLanguages` count, the homepage is the site address, every topic matches `^[a-z0-9][a-z0-9-]{0,49}$` and avoids `msix/listing/forbidden-terms.txt` except a bundled component's credit, and the custom preview reads true; no build checks it, so a fourteenth language or a reworded positioning finds this row here |
| Privacy statement | `privacy-network`, `privacy-local` keys of the site decks | as the site | 13 | must match the Store data-safety answers below and the code |
| Install trust page (`INSTALL-TRUST`) | `trust-*` keys of the site decks, `tools/site/templates/trust.html` | as the site; linked from the three READMEs and the site's Distribution section | 13 | `InstallTrustPageTests` (section order, quoted dialogs, no protection-weakening instruction); `trust-never-*` must say what the privacy statement says |
| Store listing copy | `msix/listing/<listing-code>.txt`, `shared.txt`, `search-terms*.txt`, `forbidden-terms.txt` | `tools/store/build-store-listing-csv.ps1` -> `msix/dist/store-listing-import.csv` | 13 (en-us, ru, uk by hand) | builder: forbidden terms, required fields, round trip; en-us, ru and uk first name the pillars of `POSITIONING.md` in order (`build-site.ps1 -Check`) |
| Store "What's new" | `msix/listing/release-notes/<version>.{en-us,ru,uk}.txt` and the blocks in `msix/store-listing.md` ("What's new") | `tools/store/write-release-notes.ps1` | en, ru, uk (the other ten get English) | 1,500 characters per block; the same files feed "Site What's new" below, so a notes file and a site regeneration ship in one edit |
| Site "What's new" (`SITE-STRUCTURE` release notes) | `msix/listing/release-notes/<version>.{en-us,ru,uk}.txt`, read by `tools/site/ReleaseNotes.ps1`, and the `whatsnew-*` keys of the site decks, `tools/site/templates/whats-new.html` | `tools/site/build-site.ps1` -> `docs/whats-new.html`, `docs/<code>/whats-new.html` | notes en, ru, uk (the ten others show the English notes, marked as English); page chrome 13 | `build-site.ps1 -Check` (stale until the generator is re-run after a notes file changes) and `tools/site/Test-ReleaseNotes.ps1` (file names, headers, bullets, the date derived from the version) |
| Support page | `support-*` keys and the reused `use-5-*` keys of the site decks, `tools/site/templates/support.html` | as the site; linked from the footer of every page and the privacy statement's contact section | 13 | `SupportPageTests` (section ids in order, the contact address only through `[[email]]`, every control named with the application's own string for that language) |
| Store data-safety, certification notes, runFullTrust text | `msix/store-listing.md` ("Certification notes", "runFullTrust justification", "Privacy and age-rating declarations") | pasted in Partner Center | en | must match the privacy statement |
| Store screenshots | the running app | `tools/store/capture-store-screenshots.ps1` -> `assets/store/app-<listing-code>.png` | 13 | UI Automation language check inside the script |
| Store composed images | - | `tools/store/make-store-images.ps1` -> `assets/store/screenshot-*`, poster, box art | en, ru | none |
| Third-party notices | `THIRD-PARTY-NOTICES.txt` (ships in the ZIP, the installer and the MSIX) | - | en | none |
| GitHub Release body | the commit messages since the previous tag | `.github/workflows/release.yml` (`generate_release_notes`) | en | none - write commit messages as user-facing notes |
| winget manifest | `winget/templates/` | copied per release | en-US, ru-RU, uk-UA | `winget validate`; `WingetTemplateTagTests` |
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
