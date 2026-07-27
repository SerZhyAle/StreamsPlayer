# Phase 10 - Store listing pipeline

**Status:** Implemented

Decision 1 and criteria 9, 11, 12. The existing step is replaced, not extended. Audited defects in
`tools/store/merge-listing-csv.ps1`: it writes a BOM (`:57`), overwrites cells unconditionally
(`:45-51`), hardcodes two language columns and **adds a column the export does not have**
(`:38-41`) - which is how a language gets silently dropped on import - and `Export-Csv` regenerates
quoting and appends a trailing CRLF, so a byte-identical round-trip is structurally impossible.

1. Add `msix/listing/<listing-code>.txt` for all thirteen languages in the `@@Field` block format:
   `@@ShortDescription`, `@@Description`, `@@Feature1..n`, `@@SearchTerm1..7`. Seed `en-us` and `ru`
   from `msix/store-listing.md` and `msix/store-listing-import.csv`, dropping the sentences that claim
   two interface languages. Plain text on purpose - this is prose to be proofread, not code.
   Static check: every file holds the same `@@` key set and no file mentions a language count.

2. Add `tools/store/build-store-listing-csv.ps1`, taking a fresh Partner Center export as its column
   contract. Language columns come from the export header (`$columns | Select-Object -Skip 4`), never
   from a list, so a language absent from the submission is simply not written and no unknown column
   is invented. Fill **only** empty cells, so listing-asset URLs tied to the current submission id and
   any hand-typed text pass through untouched. Write with an explicit writer -
   `New-Object System.Text.UTF8Encoding($false)` plus `[System.IO.File]::WriteAllText` - quoting every
   field, joining with CRLF, and emitting **no** trailing newline.
   Static check: the output has no BOM, every field quoted, CRLF endings, no final newline.

3. Force `OverrideLogosForWin10` to `False` for any language without its own `StoreLogo` rows. Copying
   that flag between languages holds listings Incomplete with nothing shown on the page; it stranded
   ten listings once. Take `Title` and `CopyrightTrademarkInformation` from `en-us` as shared rows -
   they are identifiers, not prose.
   Static check: no language column receives a `True` logo-override flag it did not already have.

4. Add the `-FillNothing` mode required by criterion 11: given nothing to fill, the output must be
   byte-identical to the export. Add `msix/store-listing-export.sample.csv` as a committed fixture so
   the round-trip is checkable without a live Partner Center session, and document that a real run
   needs a freshly re-taken export - the export defines which columns an import will accept and
   carries the current submission's asset URLs.
   Static check: `-FillNothing` against the fixture reports a byte-identical round-trip.

5. Add `-ImportFolder` staging: the CSV beside the screenshots in `msix/store-listing-import/`, each
   language's `DesktopScreenshot` pointing at its own file by relative path, and exactly one `.csv` in
   the folder. A relative image path is accepted only by the folder upload; in a flat CSV upload it
   fails per cell. The flat CSV therefore carries no image paths at all - blank image cells are safe.
   Static check: the staged folder holds one CSV and thirteen PNGs, and the flat CSV has no image path.

6. Add the completeness report and the search-term check. Report per language whether description,
   short description and every feature and search term are present - a listing counts as complete only
   with a description **and** at least one screenshot, and a text-only language sits at Incomplete with
   no error shown. For criterion 12, search terms stay English across all thirteen columns (owner
   decision), so the check runs over one set: at most seven unique terms, and none matching
   `msix/listing/forbidden-terms.txt`, a committed list of third-party product names seeded from the
   recorded policy 10.1.3 rejection and from the `IPTV player` risk already flagged in
   `STORE_PUBLISHING.md:133-145`. Fail the run, do not warn.
   Static check: the script exits non-zero when a forbidden term or an eighth term is present.

7. Delete `tools/store/merge-listing-csv.ps1` and the generated
   `msix/store-listing-import.filled.csv`. Leaving a tool that writes a BOM next to one that does not
   invites the wrong one being run.
   Static check: `rg 'merge-listing-csv' .` returns only historical `PLAN/DONE` references.

## Checks

- Deck inventory - expected: 13 language files with one field set | actual: 13 x 12 fields
  (`ShortDescription`, `Description`, `Feature1..10`), plus `shared.txt`, `search-terms.txt`,
  `forbidden-terms.txt` and a `README.md`.
- **Byte-identical round trip** - expected: `-FillNothing` reproduces the fixture exactly | actual:
  `Round trip is byte-identical: 44937 bytes, 453 rows, nothing filled.`, exit 0.
- Fixture form - expected: no BOM, every field quoted, CRLF, no trailing newline | actual: first bytes
  `34 70 69` (`"Fi`), last byte `34` (`"`), 17 columns x 454 lines.
- **Against a real export** (453 rows, BOM, only `en-us` and `ru` columns) - expected: parses, reports
  the eleven missing columns, fills only empty cells | actual: exactly that; `Filled 2 cell(s)`, eleven
  cells reported as `already has content, left alone`, and the BOM reported and dropped.
- `-ReplaceCopy` against the same export - expected: the stale claims are replaced | actual:
  `Filled 13 cell(s)`, including `Feature6 / en-us: replaced 29 character(s)` (the live listing's
  "English and Russian interface") and `SearchTerm7 / en-us: replaced 11 character(s)` (`IPTV player`).
- Forbidden term - expected: non-zero exit, no file written | actual: exit 1,
  `search term 'IPTV player' contains the forbidden term 'iptv' - piracy signal ..`, no output file.
- Eighth term - expected: non-zero exit | actual: exit 1,
  `search-terms.txt holds 8 terms; Partner Center accepts at most 7. Extra: one term too many`.
- Superseded files - expected: gone | actual: `merge-listing-csv.ps1`, `store-listing-import.csv` and
  `store-listing-import.filled.csv` deleted; no reference outside `PLAN/DONE/`.

### The plan's "fill only empty cells" rule would have preserved the stale claim forever

Running the builder against the live export made this obvious: `Feature6 / en-us` already said
"English and Russian interface", so a fill-only-empty pass left it alone - correct by the rule, and
exactly wrong for a ticket whose criterion 14 is *no stale claims*. Filling only empty cells protects
asset URLs; it also freezes every claim already published.

So `-ReplaceCopy` was added. It is safe by construction rather than by care: the decks name only prose
fields (`ShortDescription`, `Description`, `Feature*`, plus the shared identifiers and the search
terms), so no image, logo or trailer row is reachable from it at all. Every replacement is printed with
the length of what it displaced, and the default run now ends by telling you the switch exists when it
finds copy it refused to touch.

### Three things moved out of the per-language decks

Not per language, because they must not differ between languages:

- `shared.txt` - `Title` and `CopyrightTrademarkInformation`. The Store title is the reserved app name
  in every market.
- `search-terms.txt` - seven English terms written into all thirteen columns (owner decision), so
  criterion 12 checks one set instead of thirteen.
- `forbidden-terms.txt` - 23 entries, each with its reason, matched case-insensitively as a substring.
  Two reasons appear: policy 10.1.3 (another product's name) and the recorded infringing-content
  review risk (`iptv`, `m3u playlist`).

That also keeps the thirteen language files at an identical field set, so the parity check is a plain
set comparison.

### Deviations from the plan, and why

- **The fixture is stored in canonical import form, not as a raw export.** A real export carries a BOM;
  an import must not have one. A byte-identical round trip against a BOM-carrying file is therefore
  impossible by definition. The fixture is the *import* form (no BOM, all quoted, CRLF, no trailing
  newline), the round trip is byte-exact against it, and normalizing a real export is a separate
  documented step the script reports out loud. The reader/writer path is still proven lossless over all
  453 rows, embedded newlines and empty cells included - which is the failure mode that matters.
- **Output goes to `msix/dist/` by default**, which is gitignored, instead of beside the source. A
  generated import file and a staging folder full of copied PNGs are build output, not source.
- `.gitattributes` marks the fixture `-text`. Without it git would normalize its line endings on
  checkout and the byte comparison would fail on a fresh clone - a check that only passes on the
  machine that wrote it is not a check.
- **A shipped language with no column in the export is a hard error**, not a warning
  (`-AllowMissingLanguages` opts out). Silently shipping 2 of 13 languages is the precise failure this
  phase exists to prevent, and Partner Center says nothing when it happens.
- `msix/store-listing.md` was **not** deleted but reduced to the submission profile, requirements,
  certification notes and the runFullTrust justification, with the per-language copy removed and a line
  saying where copy lives. Two sources of listing prose is how a corrected claim survives.
