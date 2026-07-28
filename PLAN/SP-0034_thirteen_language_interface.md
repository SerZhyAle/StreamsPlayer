# SP-0034: Thirteen-language product surface

**Status:** BlockNeedUserTest - all sixteen criteria implemented, every automatic check green (299 tests, release-parity gate, byte-identical listing round trip, 13 verified captures), and criteria 3, 4, 6, 7 and 12 observed. Exit: the owner opens the language picker, chooses `العربية`, then opens Settings and Add stream, and confirms neither dialog clips or overlaps under the mirrored layout - the one thing the thirteen main-window captures do not show.

## Goal

Ship StreamsPlayer in thirteen languages: the application interface, the GitHub Pages
site, and the Windows Store listing with a matching screenshot per language - so that
the language a user reads the store page in is the language the application speaks
after install.

The thirteen, matching the set already published by the sibling product CyrFlip:
**English, Russian, Ukrainian, German, Italian, Spanish, French, Brazilian Portuguese,
Simplified Chinese, Hindi, Bengali, Arabic, Urdu.**

## Why

Today the product speaks three languages in the app and on the site, two in the Store
and winget listings, and its own text still claims "English and Russian" in a dozen
places. Every outward surface therefore under-sells the app or contradicts it.

The catalog itself is global - live TV, radio and RTSP from every region - while the
interface that browses it assumes a Slavic or English reader. The single largest
addressable audience for a free global stream browser is precisely the one that cannot
currently read its buttons.

The in-app infrastructure already scales: a keyed resource dictionary per language,
swapped at runtime with no restart, and the same shape on the website. Adding languages
there is volume, not new mechanism. What is genuinely new is right-to-left layout, a
picker that scales past three entries, and a gate that proves thirteen key sets stay
identical.

## Precedent

This is not a first attempt in this portfolio. **CyrFlip** (`P:\WINDOWS\CyrFlip`) is
already published to the Microsoft Store in exactly these thirteen languages, with a
thirteen-language interface, a thirteen-language site, a thirteen-language listing, and
one generated screenshot per language. Its `msix/` folder carries a working listing
generator and, more valuably, a written record of the Partner Center failures that were
paid for once already.

**StreamsPlayer must adopt that pipeline rather than invent one.** Its current
two-language listing tooling is not merely narrower - it reproduces at least one defect
CyrFlip has already diagnosed as fatal to the import (see Constraints).

This also settles the language set: matching CyrFlip exactly means one glossary, one
tooling shape, and one set of Partner Center columns across the portfolio. Indonesian
was considered and dropped in favour of Italian for that alignment; the two-letter codes
`id` and `it` differ by one character, and maintaining two near-identical sets in one
pair of hands invites exactly the mistake that difference would cause.

## Scope by surface

| Surface | Languages |
|---|---|
| Application interface | 13 |
| GitHub Pages site | 13 |
| Store listing text | 13 |
| Store screenshots | 13 (one per language) |
| README set | 3 - English, Russian, Ukrainian |
| winget locale files | 3 - English, Russian, Ukrainian |
| Store "What's new" / release notes | 3 - English, Russian, Ukrainian |

The three-language surfaces follow CyrFlip's deliberate choice. README and winget
readers are overwhelmingly English-reading, and thirteen README mirrors would have to be
re-synchronised on every feature change forever. Ukrainian is added to the winget locale
set, closing a gap that exists today.

## Non-goals

- **Do not translate catalog data.** Channel titles and the category, language and
  country facet values come from the published stream bank as-is and stay in their
  source language. This ticket localizes the interface, not the content.
- **Do not localize maintainer material.** Agent method documents, publishing runbooks,
  the standalone product specification, and contributor guidance stay English-only.
- **Do not expand README or winget beyond three languages** (see Scope by surface).
- **Do not weaken the explicit-refresh contract.** No language pack, dictionary or
  listing text may be fetched over the network, at startup or otherwise. Everything
  ships inside the build.
- **Do not change the MANUAL/IMPORTED merge protection or any catalog contract.**
- **Do not add user-visible features** under cover of this work. The only new surfaces
  are the language picker and whatever a right-to-left layout demands.
- **Do not block on native proofreading** (see Decision 4) - but equally, do not claim
  proofread quality anywhere in user-facing text.

## Decisions

1. **Adopt the CyrFlip listing pipeline, do not write a new one.** Its shape is proven
   against Partner Center: a per-language plain-text copy deck, a builder that takes a
   *fresh* Partner Center export as its column contract and fills only empty cells, and a
   staged import folder carrying the screenshots. StreamsPlayer's existing two-language
   merge step is replaced by that shape, not extended.

2. **Right-to-left is in scope.** Arabic and Urdu are shipped as fully mirrored layouts,
   not as right-to-left text poured into a left-to-right window. Directional glyphs and
   asymmetric controls mirror with the layout; media transport controls, which are
   direction-independent by convention, do not.

3. **One source of truth for the shipped language list.** The set of shipped languages is
   declared once and every surface that states it derives from or is checked against that
   declaration. The current situation, where "EN/RU" is asserted independently in roughly
   a dozen files and has already drifted from reality, must not be reproduced
   thirteen-fold.

4. **Machine translation, gated mechanically, honestly labelled.** Translations are
   agent-produced without native proofreading, exactly as CyrFlip's are, and the product
   says so in plain text where a user can see it. Quality is guarded by what can be
   checked mechanically - key parity, placeholder integrity, no untranslated leftovers, no
   truncation - and by a product glossary fixing how the recurring terms (stream, catalog,
   channel, refresh, pinned, collection, preview) are rendered in each language, so one
   concept is not named three ways in one window. Per-language proofreading is legitimate
   future work, one ticket per language, and is not a blocker here.

5. **First run follows the system.** With no saved preference, the interface language is
   taken from the operating system UI culture and falls back to English when it does not
   match a shipped language. An existing saved preference is always honoured and never
   overridden.

6. **The picker becomes a real control.** A flat thirteen-item checkable menu behind a
   two-letter badge does not scale and is already ambiguous (the Ukrainian ISO code reads
   as "United Kingdom"). The picker becomes a dedicated selection surface listing each
   language by its own endonym, keyboard-navigable, with the active language clearly
   marked, and reachable without knowing any of the other twelve languages.

   **2026-07-28 UI adjustment:** the main-window entry point uses an outlined globe and
   the localized `Language` label (for example, «Язык» in Russian). A flag is deliberately
   not used: it represents a country rather than an interface language.

7. **An unknown saved language must never cost the user their state.** Local state is a
   single file holding the language alongside catalog, collection and window data. A
   preference written by a newer build and read by an older one must degrade to English
   and keep the rest of the state, not fail the load and present an empty catalog. This is
   a latent defect today and thirteen languages make it reachable.

8. **Remove pseudo-plurals rather than import a plural engine.** The handful of strings
   that today fake grammatical number with a parenthesised suffix are rephrased into forms
   correct without agreement. Languages in this set carry between two and six plural
   forms; the choice is between rewriting a few strings and adopting a full plural
   framework, and rewriting is the proportionate answer.

9. **One screenshot per language, generated by driving the app.** Thirteen images, not
   thirteen sets - a single well-chosen view per language satisfies the Store and is what
   CyrFlip shipped. Each is produced by setting the language, launching the app, capturing
   the window and composing it onto a fixed Store-valid canvas, so the set can be
   regenerated wholesale when the interface changes. Screenshot captions stay empty, as
   they are in the published precedent.

   Two things do not transfer from CyrFlip and must be re-solved: it drives language
   through a registry value and a process restart, whereas StreamsPlayer holds the
   preference in its state file; and StreamsPlayer's catalog is remote content, so a
   capture depends on a populated catalog and on which channels happen to be in it.

   The Arabic and Urdu captures double as the run-and-observe evidence for mirrored
   layout - and are the one case where a naive capture is known to produce a
   *horizontally flipped* image rather than a failure.

10. **Site language selection scales and is machine-readable.** Thirteen fixed buttons do
    not fit the page header, and the site's current two-branch locale guess cannot express
    thirteen. Selection becomes a compact control, and each page declares its alternate
    language versions so search engines index them, which is the point of translating a
    landing page at all.

11. **Fail loudly rather than emit a mislabelled asset.** Any step that produces
    per-language output - a dictionary, a listing column, a screenshot - must fail when it
    cannot reach the requested language. The existing capture path does the opposite: fed a
    language it does not know, it silently captures the *previous* language's window and
    writes it under the new language's name. An image that looks correct and is wrong
    passes every automatic check and certification too.

## Constraints

- Core stays platform-neutral. The persisted language value lives in Core because state
  does; dictionaries, layout direction and the picker stay in the app.
- A missing key is a runtime defect, not a build error - the lookup falls back to printing
  the key itself. With thirteen dictionaries this becomes near-certain without an automatic
  parity gate, and that gate must run in continuous integration, not by hand.
- The existing convention holds: ISO codes in code and state, display labels are text only.
- Accessibility does not regress. Every localized control keeps its automation name and
  description, in the selected language, including under mirrored layout.
- Non-Latin scripts must render with fonts available on a supported Windows install, with
  no bundled font files and no missing-glyph boxes.
- Layout must absorb the longest translation in the set without clipping or overlapping;
  German and Hindi expand well past the widths the current layout was tuned for.
- Dictionaries are non-ASCII text files whose encoding has been silently corrupted before
  by the wrong editing tool. The process for producing them must make that failure
  impossible, not merely unlikely.
- Package size and startup time must not regress noticeably from carrying ten more
  dictionaries.

### Partner Center constraints, learned the expensive way

These are recorded facts from the CyrFlip submissions, not predictions. Each one silently
broke a listing at least once.

- **The listing import writes a file with a byte-order mark and Partner Center then
  refuses its own export format.** StreamsPlayer's current listing step writes exactly
  that. Output must be UTF-8 without BOM, every field quoted, CRLF, no trailing newline.
- **Additional languages must be added by hand in Partner Center first.** The import does
  not create them, and a language absent from the submission is dropped *silently* - one
  language's entire copy vanished this way with no error shown.
- **The import is all-or-nothing.** One rejected cell discards the whole submission,
  including fields that were valid.
- **A relative image path is accepted only by the folder upload, never by the flat CSV
  upload**, where it fails per cell with an unhelpful message.
- **A listing counts as complete only with a description *and* at least one screenshot.**
  Text-only languages sit in Incomplete indefinitely without an error on the page.
- **Never copy the Win10 logo-override flag between languages.** It silently holds
  listings Incomplete until override images exist; copying it along with shared fields
  stranded ten listings once with nothing visible on the page.
- **The export must be re-taken before every import.** It carries the current submission's
  asset URLs and defines which language columns the import will accept.
- **Search terms are a certification hazard.** A submission was rejected under store policy
  10.1.3 for naming a competing product in a search term. The limit is seven unique terms,
  and the check must cover all thirteen languages including transliterations.
- **Captured windows under right-to-left layout come back mirrored** from the capture API
  and must be flipped back, or Arabic and Urdu ship reversed.

## Acceptance criteria

1. The application offers all thirteen languages; selecting any one relabels every window,
   menu, dialog, filter option, status line and settings caption into that language with no
   English and no resource-key leakage.
2. The thirteen dictionaries hold identical key sets, identical placeholder sets per key,
   and no untranslated source strings - proven by an automatic check that fails the build
   or the pipeline, not by manual counting.
3. Arabic and Urdu present a mirrored interface: reading order, control alignment, panel
   order and directional glyphs all follow right-to-left, with no clipped or overlapping
   controls and no stranded left-to-right islands.
4. A fresh install on a system whose UI culture is one of the thirteen starts in that
   language; on any other system it starts in English; an existing saved preference is
   preserved in both cases.
5. The choice persists across restart, and a state file written by an older build still
   loads unchanged.
6. A state file naming an unknown language loads successfully into English with catalog,
   collections and window preferences intact.
7. The language picker lists all thirteen by endonym, marks the active one, is fully
   keyboard-navigable, and carries automation names in the active language.
8. The site serves all thirteen languages, selects one automatically on first visit,
   remembers the choice, and declares its alternate language versions to search engines.
9. The Store listing carries all thirteen languages - short description, description,
   features and search terms - produced by a builder that takes the language set from the
   current Partner Center export rather than naming languages individually, and that
   fills only empty cells.
10. Thirteen screenshots exist, one per language, at a Store-valid size, each generated by
    driving the application and each verifiably showing the interface in its own language;
    the Arabic and Urdu images read right-to-left and are not mirrored. A capture run that
    cannot reach a requested language fails rather than emitting a mislabelled image, and
    the owner's installed state is intact afterwards.
11. Every Partner Center constraint above is satisfied by the produced artifacts -
    verified at minimum by a round-trip in which the builder, given nothing to fill,
    reproduces the export byte for byte.
12. Search terms in all thirteen languages name no third-party product, in any script or
    transliteration, and number no more than seven per language.
13. The winget locale set covers English, Russian and Ukrainian, adding the Ukrainian file
    missing today; the README set stays at those same three.
14. No surface anywhere still claims the product ships two or three interface languages,
    and the shipped-language list is asserted in exactly one place.
15. No text is fetched at runtime; a catalog refresh neither downloads nor alters any
    translation asset.
16. Build and tests pass, the parity gate passes, and the interface, right-to-left layout,
    first-run detection and unknown-language recovery are confirmed by run-and-observe
    evidence recorded as `expected: ... | actual: ...`.

## Risks

- **Unverifiable text quality.** Machine translation produces fluent, syntactically valid,
  occasionally wrong strings, and no mechanical gate catches a plausible mistranslation.
  Decision 4 bounds this with a glossary and honest labelling, and the same trade was
  accepted for CyrFlip, but the residual risk is real. Hindi, Bengali and Urdu carry the
  highest exposure because the owner cannot spot-check them at all.
- **Right-to-left is a different kind of work.** It touches layout in every window rather
  than adding files, and it is the one part of this ticket that can fail visually while
  every automatic check passes. If it threatens the rest, it is the natural piece to split
  into its own ticket - the other eleven languages do not depend on it.
- **The screenshot depends on remote content.** Unlike CyrFlip's settings window, a
  StreamsPlayer screenshot shows a live catalog: it needs one present to capture at all,
  its contents differ run to run, and it puts third-party channel artwork in front of
  eleven new markets whose content expectations the product does not control. The Store
  already requires checking that artwork suits the selected markets; this multiplies that
  review and is not something a build agent can do.
- **Silently wrong assets.** The failure mode that matters is not a missing artifact but a
  correct-looking one in the wrong language, which passes every automatic check and
  certification. Decision 11 exists because the current capture path produces exactly this.
- **Store operations stay outside continuous integration.** Generation needs a real
  desktop, a stable screen size and a populated catalog; submission needs manual language
  setup and a fresh export every time. This is the part most likely to rot between
  releases.
- **Every future string costs thirteen.** After this ticket, adding one user-facing string
  means thirteen translations and a parity gate that refuses the change until they exist.
  That is the intended cost, but it permanently changes the economics of small UI work.
- **Layout regressions in the existing three.** Widening the layout to absorb German and
  Hindi can degrade the English, Russian and Ukrainian appearance already shipped and
  observed.
- **Glyph coverage.** Devanagari, Bengali, Arabic and CJK depend on fonts present on the
  target Windows install; a stripped or non-standard installation can show missing-glyph
  boxes that never appear on the development machine.
- **Encoding corruption.** A confirmed prior incident corrupted these exact dictionary
  files through the wrong editing tool. Ten new non-ASCII files multiply the exposure.
- **Interface localized, content not.** A Hindi reader gets Hindi buttons and English facet
  values. This is a deliberate boundary (non-goal 1), but it is the mismatch a user is most
  likely to report as a bug.

## Open questions

None. The language set (aligned to CyrFlip, Italian rather than Indonesian), the
per-surface coverage, right-to-left inclusion, machine translation without proofreading,
system-locale detection with a redesigned picker, and one generated screenshot per
language were settled with the owner on 2026-07-27.

## Last Audit

Audited 2026-07-27 against the live working tree. **Nothing has been implemented.** No tactical
plan folder exists (`PLAN/SP-0034_thirteen_language_interface/` absent), and no criterion is met.
Status stays **Approved**. Evidence dumps: `temp/SP-0034/`.

Build and test gates were deliberately **not** run: the tree carries 52 uncommitted files from
SP-0031/SP-0032, so a green build would prove nothing about this ticket and would conflate diffs.

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | 13 languages in app | FAIL | 3 dictionaries only; `AppLanguage` has 3 members (`Models.cs:40-45`) |
| 2 | Key/placeholder parity gate | FAIL | No localization test anywhere; `tests/` has zero `x:Key`/`ResourceDictionary` hits |
| 3 | Mirrored ar/ur | FAIL | `FlowDirection`/`RightToLeft`: **zero** source matches in `src/` |
| 4 | First-run system culture | FAIL | No `InstalledUICulture` in `src/`; and `CatalogState.Language` is non-nullable, so "unset" is indistinguishable from "English" (`Models.cs:212`) |
| 5 | Persists across restart | PASS (pre-existing) | `Save_PreservesLanguageAndWindowPreferences` (`StreamCatalogStoreTests.cs:107`) |
| 6 | Unknown language degrades | **FAIL - live data-loss defect** | `LoadAsync` has no `try` (`StreamCatalogStore.cs:38-48`); see finding A |
| 7 | Picker: 13 endonyms, keyboard | FAIL | Checkable `ContextMenu` behind a 2-letter badge (`MainWindow.xaml:30-36`) |
| 8 | Site 13 languages + hreflang | FAIL | `grep -c hreflang docs/*.html` → expected 13 per page \| actual **0**; 3 language keys in `site.js`; no per-language dirs |
| 9 | 13-language listing builder | FAIL | `merge-listing-csv.ps1` hardcodes en/ru (`:31,38-41`), overwrites unconditionally (`:45-51`) |
| 10 | 13 screenshots, fail-loud | FAIL | expected 13 captures \| actual 2 (`app-{en,ru}-*.png`); see finding B |
| 11 | Partner Center constraints | FAIL | `Export-Csv -Encoding utf8BOM` (`:57`); `Export-Csv` also forces a trailing CRLF and regenerates quoting, so byte-for-byte round-trip is structurally impossible |
| 12 | Search terms ≤7, no rival | WARN | 7 per language, but only 2 languages, and no mechanical check exists. `IPTV player` (`store-listing-import.csv:41`) is already flagged as a policy risk in `STORE_PUBLISHING.md:133-145` |
| 13 | winget en/ru/uk | FAIL | `winget/templates/` has en-US + ru-RU only; no `uk-UA` |
| 14 | One declaration, no stale claims | FAIL | Language set declared in **6** places (`AppLanguage`, `Available`, `DictionaryCode`, `CultureCode`, `NativeName`, `ShortCode`) + `ProductInfo.InstructionsUrl`; ~40 stale two-language claims across README/docs/winget/msix/STORE_PUBLISHING |
| 15 | No runtime text fetch | PASS | No `HttpClient`/`DownloadString` in the localization path; dictionaries compile to BAML |
| 16 | Build, tests, gate, observed | FAIL | Gate does not exist; no run-and-observe evidence recorded |

### Findings that exceed the ticket's own description

**A. Criterion 6 is not "presents an empty catalog" - it destroys the state file.** An unknown
`"language"` value throws `JsonException` for the whole document (`StreamCatalogStore.cs:46`).
`MainWindow_Loaded` catches it (`MainWindow.xaml.cs:156-161`) and leaves `_state` at its field
initialiser (`:34`). `_preferencesLoaded` never becomes `true`, so the language button stays
disabled and the user cannot even pick a language to recover - but several save paths do **not**
check that flag (volume, add stream, refresh catalog, import, history), and the first one to run
commits the empty state over the real file via `File.Move(..., overwrite: true)`, after which
`RemoveUnreferencedFiles` deletes the favicon atlas. That destroys `MANUAL`/`IMPORTED` rows, i.e.
the same user-data guarantee the merge contract protects. Separately, `{"language":99}` is accepted
silently and persists as `(AppLanguage)99`. The fix must degrade at **field** level; a document-level
`catch → new CatalogState()` would convert the crash into silent catalog loss.

**B. The two published screenshots are probably both Ukrainian.** `auto-capture.ps1:54` matches
`(English|Russian)` - the *old* value, not the requested one - and `[regex]::Replace` is a silent
no-op when nothing matches. With the owner's state set to `Ukrainian`, both `app-en-*.png` and
`app-ru-*.png` were captured from a Ukrainian window and written under English/Russian names.
Their language is unverified; treat the existing assets as compromised, not as a starting point.

**C. Criterion 4 needs a Core type change.** `CatalogState.Language` is a non-nullable enum with no
initialiser, so absence of preference reads as `English`. "Honour a saved preference, never override
it" is unimplementable until the field can express "unset" (`AppLanguage?`). Older builds always
serialize the field, so *absent key* is a sound "never chosen" signal and criterion 5 survives.

### Corrections to the ticket

- **`msix/AppxManifest.xml:15` declaring one `<Resource Language="en-us"/>` is not a gap.** CyrFlip
  ships 13 listing languages with the same single-language manifest, and records that a listing
  language is independent of the package resource set (`CyrFlip/STORE_PUBLISHING.md:117-120`).
  StreamsPlayer's manifest text also names no language count, so it is clean for criterion 14.
- **CyrFlip's key parity is enforced by the compiler, not by a gate.** All 13 translations of a
  string are arguments of one `Add(...)` call, so key sets cannot diverge. "Adopt that shape" can
  therefore only mean adopting the *gate discipline* (parity fact + untranslated-leftover detector +
  clipping check + a self-test proving the gate can fail). Key and placeholder parity for 13 XAML
  dictionaries is new code with no precedent to copy.
- **CyrFlip has no glossary.** Its term consistency is structural - a translator sees all 13
  variants on one screen. Thirteen separate dictionaries lose that, so Decision 4's glossary is
  compensation for the chosen storage form, not a transfer from the precedent.
- **The RTL mirrored-capture hazard is not yet reachable.** It afflicts `PrintWindow`; the current
  path uses `CopyFromScreen` (`auto-capture.ps1`). The switch to `PrintWindow` is needed anyway (to
  keep foreign windows out of frame), so the flip-back must be planned together with it.

### Next step

`$streamsplayer-spec-tech`. Two things need the owner before execution, not before planning:
the 52 uncommitted SP-0031/SP-0032 files touch `Models.cs` and `StreamCatalogStore.cs`, which
phases 01-02 must also edit; and criterion 12 needs an owner-supplied list of forbidden
third-party product names, including transliterations, since no such list exists in the portfolio.

## Implementation record - 2026-07-27

Thirteen phases, all `Implemented`; the per-phase `## Checks` blocks hold the evidence and the
deviations. Criterion verdicts against the live tree, replacing the audit table above:

| Criterion | Verdict | Evidence |
|---|---|---|
| 1 interface in 13 languages | PASS (partly observed) | 13 dictionaries x 313 keys; `assets/store/app-*.png` shows the main window in each. Other windows are covered by the gate, not observed - see phase 13 "What is still unobserved" |
| 2 parity gate | PASS | `LocalizationParityTests` (9 facts) + `LocalizationGateSelfTests` (12); proven to fail on three defects injected into the shipped `Localization.de.xaml` |
| 3 mirrored ar/ur | PASS observed | `app-ar.png`, `app-ur.png`; transport glyphs did not mirror |
| 4 first launch follows the OS | PASS | Observed for the absent-property path; the culture mapping itself by `InterfaceLanguagesTests` (35 cases) |
| 5 persists; an older file still loads | PASS | `AppLanguage?` with `JsonIgnoreCondition.WhenWritingNull`; `CatalogStateLanguageTests` |
| 6 unknown language keeps state | PASS observed | 3688 channels, 1 MANUAL, 7 pins, 1 hidden URL, all window preferences and the byte count unchanged through a `"Klingon"` launch |
| 7 picker | PASS | `LanguageWindow`; localized automation name verified through UI Automation |
| 8 site | PASS | 26 static pages, 13 `hreflang` + `x-default` each, generator idempotent |
| 9 listing builder | PASS | `tools/store/build-store-listing-csv.ps1`; columns from the export, fill-only-empty by default |
| 10 thirteen screenshots | PASS | 13 PNGs at 1366x768, each verified in its own language before it was written |
| 11 Partner Center constraints | PASS | Byte-identical round trip over 453 rows; no BOM, all quoted, CRLF, no trailing newline |
| 12 search terms | PASS | One English set of 7; forbidden-term and count checks exit non-zero |
| 13 winget uk-UA, README set stays 3 | PASS | Five templates; three READMEs state the count once |
| 14 one declaration, no stale claims | PASS | `InterfaceLanguages` read by reflection from the built assembly by all PowerShell tooling; claim grep clean outside history |
| 15 no runtime fetch | PASS | Asserted mechanically; the site now ships no translation table at all |
| 16 build, tests, gate, observed | PASS | `scripts/check.ps1` green; observed items recorded in phase 13 |

Three things the plan did not anticipate, all recorded where they were found:

- **WPF does set `WS_EX_LAYOUTRTL`.** The conditional flip-back is not dead code - without it two of
  the thirteen Store screenshots would have shipped mirrored, and nothing automatic would have said so
  (phase 11).
- **Choosing Arabic changed the calendar, not just the wording.** `ar-SA` defaults to Umm al-Qura, so
  the catalog timestamp read `1448/02/12` while the rest of the desktop said 2026-07-26. Fixed in
  `LocalizationService`; found only because criterion 10 forces a look at a real Arabic window
  (phase 13).
- **"Fill only empty cells" would have frozen every published claim.** The live listing still said
  "English and Russian interface", and a fill-only-empty pass left it there. `-ReplaceCopy` added, safe
  by construction because the decks name only prose fields (phase 10).

Parked, not fixed: **SP-0035** (the same tolerant-enum defect for the four remaining persisted enums)
and **SP-0036** (a pre-existing flaky ICY metadata test, observed once during this validation).

One decision left for the owner beyond the exit condition: `tools/site/copy/uk.txt` keeps the Latin
`STREAMS Player`, while `Localization.uk.xaml`, the glossary and the new Ukrainian Store deck all use
`Трансляції`. Ukrainian is the one language where the site and the application name the product
differently. Not changed unilaterally - it is the owner's own language and his own published prose.
