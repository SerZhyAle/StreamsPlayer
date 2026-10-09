# SITE-EXPERIENCE - pointer

| | |
| --- | --- |
| **Id** | `SITE-EXPERIENCE` |
| **Version** | 0.3 (draft) |
| **Home** | `Contracts/product-site/SITE-EXPERIENCE.md`, with the run-list `SITE-CHECKLIST.md` beside it |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer at the page tier**, opted in 2026-10-06 (SP-0192) |

## Why it applies here

Rules 1, 7, 13, 14 and 15 to 19 bind a page-tier site. The portal-layer rules (2 to 6, 8 to 12) do not: there is no
portal, no search and no function pages.

## Where this repository meets it

- **Third-party origins** (rule 14): the pages contact `fonts.googleapis.com` and `fonts.gstatic.com` (head of every
  page, `tools/site/templates/_head.html`) and `api.github.com` (one `fetch` in `docs/site.js` that resolves the
  installer link, home page only), plus the host itself, GitHub Pages (`serzhyale.github.io`). No analytics or advertising
  origin and no cookie. The privacy page names all four in its "This website" section (keys `privacy-site-title` and
  `privacy-site`, thirteen locales; SP-0196).
- **Versions on a page** (rule 13): the release link is resolved at run time (`docs/site.js`) and no copy deck types a
  version. One departure, declared 2026-10-09 (SP-0039): the What's new page (`whats-new.html`, thirteen locales) prints
  one version heading per release, rendered at generation time by `tools/site/ReleaseNotes.ps1` from the recorded release
  notes (`msix/listing/release-notes/*.txt`, the release's own manifest) and not from the manifest the landing resolves;
  it lists only versions that reached a channel, and `tools/site/Test-ReleaseNotes.ps1` holds the reader. The reading is
  this product's, put to the contract owner as Ask 2 of `Contracts/product-site/PROPOSAL-2026-10-09-support-page-type.md`;
  the dated exception that carries it, until 2026-12-31 or the owner's answer, is the `SITE-STRUCTURE`, `SITE-EXPERIENCE`
  row naming `support.html` in section 3 of `Contracts/_meta/REGISTRY.md`. The page is in the working tree and not yet
  published.
- **Kit** (rule 1): `docs/assets/sza-kit.css` is the catalog kit, byte-identical (`-text` in `.gitattributes`), linked before
  `docs/style.css`, which is the product layer; its SHA-256 is pinned in `tools/site/kit-provenance.txt` and
  `tools/site/build-site.ps1` fails when the served file stops matching it (SP-0195).
- **Theme and language state** (rule 7 and the page-tier part of rule 10): the PAGE-STYLE section 7 pre-paint resolver
  runs in the head before the first stylesheet and `docs/site.js` writes only the shared `sza-theme` and `sza-lang`
  (`uk` stored as `ua`). RU EN UA is the segmented control of PAGE-STYLE section 4.2; the other ten locales are a
  secondary text row and are never written to `sza-lang`, only to this product's `streamsplayer-locale`, paired with
  the shared value current at that moment (SP-0195).
- **Reach** (rules 15 to 19): skip link and `:focus-visible` rule present, one `main`, one `h1`, `lang` and `dir`
  set per page, `prefers-reduced-motion` rule in `docs/style.css`. Rule 17 is met in `docs/style.css` (SP-0197): every
  link, button and theme control is at least 28 px on a fine pointer and 44 px on a coarse one, and text measures at
  least 4.5 : 1 in both themes (the light accent button 5.37 : 1, a gold badge 5.28 : 1). The kit's light accent
  (`#2f8f3a`, 4.11 : 1 under white ink) and gold are darkened in the product layer, after the kit, until the kit's
  own light values are revised.
- **Run in a browser with real input** (rules 8, 15, 17, 19; 2026-10-07, SP-0199, Chrome 151 over the DevTools protocol on
  a local serve of `docs/`): a Tab traversal of eight pages in both themes at 1428 and 360 px (the skip link first and
  working, every control reached in source order, a solid 2 px ring on every stop); reduced motion emulated and the
  page reloaded (no transition or animation, the backdrop one still frame, no frame requested; the same on a live
  switch and in a hidden tab); 360 px on all 40 pages (no horizontal scroll, no overflowing element, no standalone
  target under 44 px on a coarse pointer); and contrast sampled from the pixels painted behind each of 8166 text
  runs (lowest 4.60 : 1 light, 5.91 : 1 dark) and from the computed colours of 1584 control states (rest, hover,
  focus). The run found three defects, fixed in `docs/style.css`: the machine-translation note drawn at 0.85 opacity
  (3.6 : 1 in the light theme), the footer heading standing on the bare backdrop (3.9 : 1 where a line crossed it),
  and the icon-only brand link 30 px wide on a coarse pointer. The published pages are re-read at the next publish.

## What this repository must do to stay conformant

1. Declare any new third-party origin in the adoption row and name it on the privacy page in the same edit.
2. Render versions and channels from the generated copy, never retype them in a page. The What's new page is the one
   departure (rule 13 above): its versions come from the recorded release notes, so a version added there is a notes
   file, never a typed string.
3. Run `SITE-CHECKLIST.md` in a browser before a release that changes the site's chrome.

Evidence: `docs/site.js`, `tools/site/templates/_head.html`, `tools/site/ReleaseNotes.ps1`, `docs/style.css`, the
registry rows, `PLAN/SP-0192` and `PLAN/SP-0199`.

**0.3, read 2026-10-07 (SP-0206).** Rule 20 (every page fills the screen) holds with the kit's `--wide: 100%`, so the dated
exception for the switched-off cap is no longer needed. Rule 10's picker form concerns a portal; this site has none and keeps
the segmented control. Rule 17's 44 px is owed for every pointer by `PAGE-STYLE` 1.4: SP-0209.

**Update, 2026-10-07 (SP-0209).** The kit was re-vendored at the 2026-10-07 revision (SHA-256 `27501a10..`,
`tools/site/kit-provenance.txt`). Rule 17's 44 px now holds for every pointer: the page layer gives `.button`, `.seg a`
and `.theme-button` the 44 px minimum in its own names (the kit carries the same rule for `.btn`, `.seg button` and
`.theme-btn`, which this page does not use). The reduced-motion rule is the kit's alone now that it reaches
pseudo-elements; the page layer dropped its copy. The theme control is labelled (PAGE-STYLE 4.3, 1.4): the `app.theme`
glyph beside the visible localized name (deck key `@@theme-name`, the same word each language's app UI uses), still with
its `aria-label`.
