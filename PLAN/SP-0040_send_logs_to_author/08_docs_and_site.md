# Phase 08 - Documentation and the product site

**Status:** Planned

## Goal

Describe the feature where a user looks for it, and stop the privacy page from claiming something
that is no longer true (spec criteria 14, 15).

## Changes

1. `README.md`, `README.ru.md`, `README.uk.md` - in the settings/About section: the button, what the
   archive contains (both session logs plus the environment summary), that the user attaches and
   sends it themselves, and that the logs include the stream URLs that were played.
2. `tools/site/copy/en.txt`, `ru.txt`, `uk.txt` and the ten machine-translated locales:
   - a new usage entry describing the button;
   - **`@@privacy-local` corrected** - it currently ends "This data stays on your device unless you
     choose to copy or back it up", which after this ticket is only true if the sentence also names
     this explicit user-initiated exception;
   - the existing `[[email]]` and `[[appdata]]` placeholders are reused verbatim, not inlined.
3. Regenerate the site: `pwsh ./tools/site/build-site.ps1`. The twenty-six `docs/**/*.html` pages
   are render targets - never hand-edited (canon invariant 16).

## Verification

- `pwsh ./tools/site/build-site.ps1` exits 0; `git status --short docs/` shows only regenerated
  pages, no stray files.
- `grep -c "privacy-local" tools/site/copy/*.txt` - expected 13 (one per locale, key set unchanged).
- `grep -rl "stays on your device unless" docs/` - expected: no matches in the generated pages.
- The site's key sets stay comparable across locales: the generator fails on a missing key, so its
  clean exit is the check.

## Checks

- Status: Implemented.
- expected: site generator exits 0 | actual: exit 0, 26 pages rewritten; `git status --short docs/` shows only regenerated pages.
- expected: no page still claims local data never leaves the device | actual: `grep -rl "stays on your device unless you choose to copy" docs/` - 0 files.
- expected: the new step and the corrected privacy text reach the localized pages | actual: `docs/index.html` carries "Report a problem with the logs"; `docs/ru/index.html` and `docs/ru/privacy.html` both name «Отправить журналы автору», which is verbatim the Russian button label.
- Deviation: `@@privacy-local` no longer names `Current.log` in any locale. The page now describes two logs, and keeping a single filename there would have been the more misleading of the two options.
- Noted, not fixed (pre-existing, out of scope): `tools/site/copy/uk.txt` uses the Latin brand throughout while `Localization.uk.xaml` and the glossary use «Трансляції».
