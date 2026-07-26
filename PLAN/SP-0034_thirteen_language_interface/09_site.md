# Phase 09 - Thirteen site languages

**Status:** Approved

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
