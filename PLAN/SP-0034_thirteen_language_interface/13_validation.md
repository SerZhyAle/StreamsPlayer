# Phase 13 - Validation

**Status:** Approved

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
