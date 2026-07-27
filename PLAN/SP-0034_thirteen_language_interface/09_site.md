# Phase 09 - Thirteen site languages

**Status:** Implemented

Decision 10 and criterion 8. The site today is two HTML pages on two URLs with all copy in a
`translations` object (`docs/site.js:1-272`), three languages, a hardcoded two-branch locale guess
duplicated in three places (`docs/index.html:22-25`, `docs/privacy.html:22-25`,
`docs/site.js:340-354`), and no `hreflang` at all.

`hreflang` needs distinct URLs, so this is an architectural change, not an added tag. GitHub Pages
deploys `docs/` verbatim with no build step (`.github/workflows/pages.yml`), so the generated pages
must be committed output. The URL scheme follows the CyrFlip precedent: per-language folders with
English at the root.

1. Add `tools/site/copy/<code>.txt` for all thirteen languages in the `@@Key` block format - the same
   format phase 10 uses for the Store copy deck, so the project has one prose format. Seed `en`, `ru`
   and `uk` from the existing `translations` object so no published copy is lost.
   Static check: every file holds the same `@@` key set.

2. Add `tools/site/build-site.ps1` rendering `docs/index.html` and `docs/privacy.html` (English, the
   canonical root) plus `docs/<code>/index.html` and `docs/<code>/privacy.html` for the other twelve,
   from templates plus the copy files. Each page carries `<html lang="..">`, `dir="rtl"` for `ar` and
   `ur`, a full set of thirteen `<link rel="alternate" hreflang="..">` plus `x-default` pointing at
   the root, and a canonical link. The generator must never write into `docs/agent/`,
   `docs/specifications/` or `docs/assets/`.
   Static check: re-running the generator produces no `git diff` - output is idempotent.

3. Replace the three-button pill (`docs/index.html:54-58`) with a compact `<select>` of thirteen
   endonyms that navigates to the selected language's URL, keeping a localized
   `aria-label` - the current `aria-label="Language"` is not localized, unlike the theme button.
   Fix the label/code mismatch: the button reads `UA` while the code is `uk`.
   Static check: every generated page holds one selector with thirteen options.

4. Replace the locale guess with a table-driven redirect that walks `navigator.languages` in order,
   matches on the base subtag, and falls back to English. It lives in exactly one place - the shared
   inline script the generator emits - and it must only redirect when there is no stored choice.
   Persist the choice under the existing `streamsplayer-lang` key, and store the same codes the
   registry uses so nothing has to translate `uk` into `ua`.
   Static check: `rg "indexOf\('ru'\)|startsWith\(\"ru\"\)" docs/` returns nothing.

5. Right-to-left pages keep technical strings in left-to-right islands - the winget command line and
   any URL get `dir="ltr"` - following the precedent's handling.
   Static check: the `ar` and `ur` pages declare `dir="ltr"` on every command and URL element.

6. Update `docs/style.css` for the mirrored pages: it currently uses seven physical-direction
   declarations against two logical ones and has no `rtl` handling at all. Convert the layout-relevant
   ones to logical properties.
   Static check: `rg 'margin-left|padding-left|left:' docs/style.css` shows no layout rule that a
   mirrored page depends on.

## Checks

- Copy decks - expected: 13 files, one key set | actual: 13 files x 93 keys, generator's parity check
  passes (key set, placeholder set and empty-value check against English).
- Generated pages - expected: 26 pages plus `site.js` | actual: 26 written, plus `docs/site.js`.
- Idempotency - expected: no change on a second run | actual: `build-site.ps1 -Check` reports
  `docs/ is up to date.` and exits 0.
- Per-page structure - expected: 1 selector, 13 options, 13 `hreflang` + `x-default`, 1 canonical |
  actual: all 26 pages, and 13 `<noscript>` links each.
- Locale-guess leftovers - expected: none | actual: 0 matches for `indexOf('ru')`, `startsWith("ru")`
  or `translations[` across `docs/`.
- Layout direction - expected: `rtl` only for ar and ur | actual: `<html lang="ar" dir="rtl">` and
  `dir="ur" rtl`; en and de carry `dir="ltr"`.
- Left-to-right islands - expected: every command and address | actual: 6 per right-to-left home page
  (three `<pre>` blocks, the winget command, the `%LOCALAPPDATA%` path, the footer address).
- Machine-translation notice - expected: on the ten machine-translated languages only | actual: on 20
  pages; absent from `index.html`, `privacy.html`, `ru/`, `uk/` - the six the owner wrote himself.

### hreflang is derived, with one rule and no per-language literal

`hreflang` is the URL code plus a *script* subtag when the culture carries one, and never a region:
`de`, `pt`, `zh-Hans`, `ar`. `hreflang="de-DE"` would signal "German, Germany" and exclude Austria and
Switzerland from the match, which is the opposite of what a single German page wants. The rule is one
sentence in `Get-HrefLang` and adding a language needs no edit.

### The language list is read from the Core registry, not restated

`tools/InterfaceLanguages.ps1` loads the built `StreamsPlayer.Core.dll` and reads
`InterfaceLanguages.All` by reflection; the endonyms come from the shipped `Localization.en.xaml`
`Language*` keys. So the site generator, the Store builder and the capture script all derive from the
same declaration the application uses (criterion 14), and a fourteenth language means editing the
registry and nothing else. The assembly is loaded from a byte array rather than `LoadFrom`, so it does
not hold the file open against a later `dotnet build`.

### Only the canonical root redirects

The `navigator.languages` walk runs on `docs/index.html` and `docs/privacy.html` only, marked
`data-entry="true"`. Redirecting from a language folder would fight a visitor who typed or followed
that URL, and on an English-preferring browser sitting at `/de/` it would bounce them straight out of
the page they asked for. Restricting it to the entry point is also what makes a redirect loop
impossible.

### Three things deliberately left physical in the CSS

The two `.glow` blobs keep `left`/`right`: they are `aria-hidden` background decoration and no layout
depends on them. The `.cap li::before` bullet needed more than a logical property - `content: "▸"` is a
*directional glyph*, which the layout engine does not mirror, so `[dir="rtl"]` swaps it for `◂`. That
is the one case where mirroring the box was not enough.

### Deviation: the switcher is a select *and* a link list

The plan asked for a `<select>`. A select does nothing without JavaScript, so each page also carries a
`<noscript>` list of the same thirteen links. The URLs come from the same generated table, so the two
cannot disagree.

### One open question for the owner: the Ukrainian brand

`tools/site/copy/uk.txt` keeps the Latin `STREAMS Player`, because the published Ukrainian site copy
did. But `Localization.uk.xaml` sets `ProductName` to `Трансляції`, `docs/localization/glossary.md`
records Ukrainian as one of the two languages that localize the brand, and the Russian site copy uses
`Трансляции`. So Ukrainian is the one language where the site and the application name the product
differently. Not changed here: it is the owner's own language and his own published prose, and picking
for him would be the wrong kind of tidy.
