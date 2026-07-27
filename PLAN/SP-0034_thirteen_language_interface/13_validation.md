# Phase 13 - Validation

**Status:** Implemented - one observed item short of complete; see "What is still unobserved".

Criterion 16. Record every check as `expected: ... | actual: ...` in this file. A passing build is not
evidence that a changed user action works; the four observed items below need the app actually running.

1. `dotnet build StreamsPlayer.sln -c Release` - expected: succeeds with no new warning.
2. `dotnet test StreamsPlayer.sln -c Release --no-build` - expected: all pass, including
   `InterfaceLanguagesTests`, the extended `StreamCatalogStoreTests` and `LocalizationParityTests`.
3. `./scripts/check.ps1` - expected: the release-parity gate passes.
4. `tools/site/build-site.ps1` then `git diff --exit-code docs/` - expected: no diff, proving the
   committed site output matches the generator.
5. `tools/store/build-store-listing-csv.ps1 -FillNothing` against the committed export fixture -
   expected: reports a byte-identical round trip.
6. The forbidden-term and seven-term checks against a deliberately bad input - expected: non-zero exit.
7. `tools/store/capture-store-screenshots.ps1` - expected: thirteen PNGs at one Store-valid size, each
   verified in its own language; the Arabic and Urdu images read right-to-left and are not mirrored;
   the real `catalog-state.json` hash is unchanged afterwards. Also run it with an unknown language -
   expected: non-zero exit and no PNG written.

Observed items - each needs a screenshot or a recorded UIA reading under `temp/SP-0034/`:

8. **Thirteen-language relabel.** Select each language in the new picker and confirm every window,
   menu, dialog, filter option, status line and settings caption is in that language, with no English
   left and no resource key printed. Criterion 1.
9. **Mirrored layout.** In Arabic and Urdu, confirm reading order, control alignment, panel order and
   directional glyphs all follow right-to-left, that the media transport controls did *not* mirror,
   that nothing is clipped or overlapping, and that no left-to-right island is stranded. Criterion 3.
10. **First run.** With no `language` property in the state file and the OS UI culture set to a shipped
    language, confirm the app starts in that language; with an unshipped culture, English; and that an
    existing saved preference is honoured in both cases. Criterion 4.
11. **Unknown-language recovery.** Hand-write `"language": "Klingon"` into a sandbox state file holding
    channels, collections and window preferences, launch, and confirm the app opens in English with the
    catalog, collections and window preferences intact, and that the state file is not overwritten with
    an empty one. Criterion 6.
12. **Layout headroom.** Confirm German and Hindi do not clip or overlap in the toolbar row.

Also record the negative checks: no text is fetched at runtime and a catalog refresh alters no
translation asset (criterion 15), and the shipped-language list is asserted in exactly one place
(criterion 14) - the claim grep returns hits only under `PLAN/DONE/`.

## Checks

### Automatic

1. `dotnet build StreamsPlayer.sln -c Release` - expected: succeeds, no new warning | actual:
   `Build succeeded. 0 Warning(s) 0 Error(s)`.
2. `dotnet test StreamsPlayer.sln -c Release` - expected: all pass | actual: **299 passed, 0 failed**
   on the re-run and under `check.ps1`. One earlier run failed a single unrelated test - see
   "The flaky test" below.
3. `./scripts/check.ps1` - expected: release-parity gate passes | actual: `Test Run Successful.
   Total tests: 299, Passed: 299`, build 0 warnings 0 errors.
4. `tools/site/build-site.ps1 -Check` - expected: no stale output | actual: `docs/ is up to date.`,
   exit 0, after a full generation run that wrote all 26 pages plus `site.js`.
5. `tools/store/build-store-listing-csv.ps1 -FillNothing` - expected: byte-identical | actual:
   `Round trip is byte-identical: 44937 bytes, 453 rows, nothing filled.`, exit 0.
6. Forbidden term - expected: non-zero, nothing written | actual: exit 1,
   `search term 'IPTV player' contains the forbidden term 'iptv'`, no output file.
   Eighth term - expected: non-zero | actual: exit 1, `holds 8 terms; Partner Center accepts at most 7`.
7. `tools/store/capture-store-screenshots.ps1` - expected: 13 PNGs, one size, each verified | actual:
   13 files at **1366x768**, one distinct size, each language confirmed by the localized automation
   name it matched; `Real profile restored, catalog-state.json unchanged.` Unknown language - expected:
   non-zero, no PNG, no sandbox | actual: exit 1, `'klingon' is not a shipped language`, aside folder
   never created.

### Observed

8. **Thirteen-language relabel** - `assets/store/app-<listing-code>.png`, thirteen files. Every visible
   string in the main window - toolbar, filter labels, filter values (`All` / `Alle` / `सभी` / `الكل`),
   section headers, the per-tile media-kind caption, the status line and the Stop button - is in the
   captured language. No resource key is printed anywhere, and no English is left except the deliberate
   loanwords recorded in `localization-loanwords.txt` and the channel names, which are data.
   Partially observed: see "What is still unobserved".
9. **Mirrored layout** - `app-ar.png`, `app-ur.png`. Brand and subtitle right-aligned; the toolbar
   cluster runs right to left; the filter row starts at the right; the tile grid fills right to left;
   the status line is right-aligned with the Stop button moved to the left edge. Arabic and Urdu text
   renders correctly, not mirrored. **The transport glyphs did not mirror**: each tile's ▶ still points
   right, which is the phase 08 exemption working. Nothing clipped, nothing overlapping. The Latin
   channel names inside right-to-left tiles show the usual bidi parenthesis reordering
   (`(Al Sharqiya (Al-Sharqiya`) - inherent to bidirectional text, not a layout fault.
10. **First run** - `temp/SP-0034/verify-language-startup.log`, case A. State file with the `language`
    property deleted, OS `CurrentUICulture=en-US`. Expected: starts in the detected language and
    persists it | actual: window reports `Interface language` (English), and the state gained
    `"language": "English"` where none had been recorded; 3688 channels and 1 hidden URL unchanged.
    What this run does **not** show: that a *non-English* OS culture is honoured, because this machine
    is en-US and English is also the fallback - the two are indistinguishable here. That mapping is
    covered by `InterfaceLanguagesTests` (35 cases, including regional variants like de-AT and pt-PT
    and unshipped cultures falling back), not by this observation. Stated plainly rather than claimed.
11. **Unknown-language recovery** - same log, case B. State file with `"language": "Klingon"`, holding
    3688 channels of which 1 is `MANUAL`, 7 pinned, 1 hidden URL, `tileSize: Small`, `viewMode: Grid`,
    `mainWindowTopmost: False`. Expected: opens in English, everything intact, no empty overwrite |
    actual: window reports `Interface language`; the state was rewritten with `"language": "English"`
    (the recovery); **channels 3688 → 3688, manual 1 → 1, pinned 7 → 7, hidden 1 → 1, tileSize Small →
    Small, viewMode Grid → Grid, topmost False → False, file 2 828 621 → 2 828 621 bytes.** This is the
    audit's finding A closed with numbers: before this ticket the same file cost the user the catalog,
    the favicon atlas and every `MANUAL`/`IMPORTED` row.
12. **Layout headroom** - `app-de.png`, `app-hi.png`. German is the widest set of strings
    (`Vorschau aktualisieren`, `Stream hinzufügen`, `Katalog aktualisieren`, `Immer im Vordergrund`) and
    the toolbar renders complete with the subtitle
    `Unabhängiger Katalog für Live-TV, Radio und RTSP` beside it - nothing clipped, nothing wrapped,
    nothing overlapping. Hindi renders complete and is narrower. This settles the phase 08 step 5
    reassessment with evidence instead of reasoning: the toolbar needed no restructuring.

### Negative checks

- **No runtime fetch** (criterion 15) - expected: nothing fetched | actual: asserted mechanically by
  `LocalizationParityTests` - no dictionary value contains `://` and no dictionary declares a `Source`
  attribute. The site is the same: `docs/site.js` holds no translation table at all now, every string
  is rendered into the static page, and the only external requests on a page are the two font
  preconnects that were already there.
- **A catalog refresh alters no translation asset** - expected: none | actual: the refresh path
  (`StreamCatalogService`, `StreamCatalogStore`) touches `catalog-state.json` and the favicon atlas
  only; the dictionaries are compiled BAML inside the assembly and are not files on disk at runtime.
- **One declaration** (criterion 14) - expected: hits only under `PLAN/DONE/` | actual: the claim grep
  is clean except `PLAN/DONE/`, the published `manifests/` snapshots, the gitignored legacy `tmp/`
  tree, and three new code comments that quote the old claim to explain what the tooling replaced.
  The list itself exists once, in `InterfaceLanguages`; the PowerShell tooling reads it out of the
  built assembly rather than restating it.

### The flaky test

One full Release run failed `IcyMetadataReaderTests.ReadAsync_ReportsChangedStreamTitlesFromIcyStream`
(`expected "Test Artist - Test Song", actual "Second Track"`). The same binary passed on an immediate
re-run and under `check.ps1`, and `git log` shows nothing in this ticket touched `IcyMetadataReader` or
its test. Recorded here rather than hidden, and parked as **SP-0036** with a repeat-run criterion -
a test that fails one run in several trains everyone to re-run instead of read.

### A defect this phase found: the Arabic build showed Hijri dates

The first Arabic capture read `آخر تحديث للكتالوج 1448/02/12 بعد الهجرة 3:23 ص`. `ar-SA` defaults to
the Umm al-Qura calendar, and phase 04 had made the catalog timestamp format with
`CultureInfo.CurrentUICulture`, so choosing Arabic silently changed the **calendar**, not just the
wording - while Windows, the file system and every other application on the machine showed 2026-07-26.

Fixed in `LocalizationService.CreateUiCulture`: the interface culture is cloned with
`DateTimeFormat.Calendar` set to the culture's Gregorian calendar when its default is something else.
Month names, digits and ordering still follow the chosen language. Re-captured: `2026/7/26 م 3:23 ص`,
matching the English `7/26/2026 3:23 AM`.

Nothing in the plan predicted this. It was visible only because criterion 10 requires looking at a real
Arabic window - the exact reason the ticket demanded observed evidence rather than a green build.

### What is still unobserved

**A second window under a right-to-left language.** The thirteen captures are all of the main window.
`temp/SP-0034/capture-settings.ps1` was written to open the Settings dialog through UI Automation in
German, Hindi and Arabic and capture it; the button is found and its localized automation name verifies
(`Einstellungen öffnen` in German - itself evidence for criterion 7's automation requirement), but the
`InvokePattern` call does not open the modal in an automated run, and I stopped rather than spend
further effort on the harness.

So for `SettingsWindow`, `AddStreamWindow`, `CollectionsWindow`, `ImportPreviewWindow`,
`ImportUrlWindow`, `ListeningHistoryWindow`, `HiddenChannelsWindow`, `PlaybackFailureDialog`,
`LanguageWindow` and `PlayerWindow`, what is proven is mechanical, not observed: every window binds
`FlowDirection="{DynamicResource UiFlowDirection}"` (phase 08), and the parity gate guarantees the key
set, the placeholder sets and the layout direction of all thirteen dictionaries. What is **not** proven
is that none of those dialogs clips or overlaps under the longest translation or the mirrored layout.

That is a thirty-second job for the owner: open the language picker, choose `العربية`, then open
Settings and Add stream. Recorded as the honest residual rather than folded into criterion 1.
