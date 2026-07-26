# Phase 10 - Store listing pipeline

**Status:** Approved

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
