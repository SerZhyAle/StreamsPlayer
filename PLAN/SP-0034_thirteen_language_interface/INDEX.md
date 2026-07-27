# Tactical plan - SP-0034

**Status:** Tactical

Thirteen shipped languages, in menu order, with the codes each surface uses:

| # | `AppLanguage` | Dictionary | Culture | Listing | RTL |
|---|---|---|---|---|---|
| 1 | `English` | `en` | `en-US` | `en-us` | no |
| 2 | `Russian` | `ru` | `ru-RU` | `ru` | no |
| 3 | `Ukrainian` | `uk` | `uk-UA` | `uk` | no |
| 4 | `German` | `de` | `de-DE` | `de` | no |
| 5 | `Italian` | `it` | `it-IT` | `it` | no |
| 6 | `Spanish` | `es` | `es-ES` | `es` | no |
| 7 | `French` | `fr` | `fr-FR` | `fr` | no |
| 8 | `Portuguese` | `pt` | `pt-BR` | `pt-br` | no |
| 9 | `Chinese` | `zh` | `zh-Hans` | `zh-hans` | no |
| 10 | `Hindi` | `hi` | `hi-IN` | `hi` | no |
| 11 | `Bengali` | `bn` | `bn-BD` | `bn` | no |
| 12 | `Arabic` | `ar` | `ar-SA` | `ar` | **yes** |
| 13 | `Urdu` | `ur` | `ur-PK` | `ur` | **yes** |

Enum member names are the persisted JSON tokens and must never be renamed. `Portuguese` and
`Chinese` follow the CyrFlip precedent - the app code is two-letter, the shipped variant is
Brazilian Portuguese and Simplified Chinese, and only the listing code spells that out.

| Phase | Produces | Consumes |
| --- | --- | --- |
| 01 | Core language registry: 13 enum members, per-surface codes, RTL flag, culture matching. The one declaration every other phase derives from | Approved spec |
| 02 | State that survives an unknown language at field level; `AppLanguage?` expressing "never chosen" | 01 |
| 03 | First-run selection from the OS UI culture, English fallback, saved preference never overridden | 01, 02 |
| 04 | Pseudo-plurals rephrased; culture-less formatting removed | none |
| 05 | Parity gate in `StreamsPlayer.Core.Tests`, including a self-test proving it can fail | 01, 04 |
| 06 | Product glossary and the ten new dictionaries | 01, 04, 05 |
| 07 | The language picker as a real control | 01, 06 |
| 08 | Mirrored layout for Arabic and Urdu; layout headroom for German and Hindi | 01, 06 |
| 09 | Thirteen static site languages, compact selector, `hreflang` | 01 |
| 10 | Store listing builder on the CyrFlip shape; 13-language copy deck; search-term check | 01 |
| 11 | Screenshot pipeline: 13 captures, fail-loud, RTL flip-back, sandboxed | 01, 06, 08 |
| 12 | Stale-claim sweep, winget `uk-UA`, corrected publishing docs | 01, 09, 10 |
| 13 | Build, tests, gate, and run-and-observe evidence | 01-12 |

Coverage of the sixteen acceptance criteria:

| Criterion | Phase |
|---|---|
| 1 interface in 13 languages | 06, 07, 13 |
| 2 parity gate | 05 |
| 3 mirrored ar/ur | 08, 13 |
| 4 first-run culture | 03, 13 |
| 5 persists; old file loads | 02 |
| 6 unknown language keeps state | 02, 13 |
| 7 picker | 07, 13 |
| 8 site | 09 |
| 9 listing builder | 10 |
| 10 thirteen screenshots | 11 |
| 11 Partner Center constraints | 10 |
| 12 search terms | 10 |
| 13 winget uk-UA, README stays 3 | 12 |
| 14 one declaration, no stale claims | 01, 12 |
| 15 no runtime fetch | 05 (asserted), 13 |
| 16 build, tests, gate, observed | 13 |

Constraint coverage: Core stays platform-neutral (01-03 add no UI type; dictionaries, direction and
picker live in App, phases 06-08). The parity gate reads the dictionaries as **files**, never as a
project reference, so `Tests -> Core` is preserved (05). Encoding safety is mechanical: dictionaries
are written only with the Write/Edit tools or `pwsh` 7, and the gate rejects a BOM (05, 06).
Accessibility names stay localized and are covered by the gate's key parity plus phase 07.

Owner decisions taken on 2026-07-27: search terms stay English across all thirteen listing columns,
so criterion 12 checks one set; the concurrent SP-0031/0032/0033 work was committed first
(`f3c2022`, `f792c08`) and this ticket runs on branch `sp-0034-thirteen-languages`.

All thirteen phases are `Implemented`. Phase 13 records the observed evidence and the one item that
stayed unobserved (a second window under a right-to-left language), which is why the strategic ticket
is `BlockNeedUserTest` rather than ready for `Verified`.

Two structural decisions taken during execution, neither in the plan:

- **The PowerShell tooling reads the language registry out of the built assembly.**
  `tools/InterfaceLanguages.ps1` loads `StreamsPlayer.Core.dll` by reflection (from a byte array, so it
  does not lock the file) and reads `InterfaceLanguages.All`, taking the endonyms from
  `Localization.en.xaml`. The site generator, the Store listing builder and the capture script therefore
  derive from the same single declaration the application uses - criterion 14 holds across languages
  *and* across tools, and a fourteenth language needs no edit outside `StreamsPlayer.Core`.
- **The Store copy deck splits per-language prose from shared rows.** `msix/listing/<code>.txt` holds
  only `ShortDescription`, `Description` and `Feature1..10`, so all thirteen files have an identical
  field set and parity is a plain set comparison. `shared.txt` (Title, copyright), `search-terms.txt`
  (one English set of seven) and `forbidden-terms.txt` sit beside them.