# Store listing copy deck

One file per Store listing language, named by the **listing code** Partner Center uses
(`en-us`, `ru`, `uk`, `de`, `it`, `es`, `fr`, `pt-br`, `zh-hans`, `hi`, `bn`, `ar`, `ur`).
`tools/store/build-store-listing-csv.ps1` reads them and fills a Partner Center export.

Format: a `@@Field` line, then the value on the following line(s), until the next `@@` or end of
file. Blank lines inside a value are kept - Partner Center renders them as paragraph breaks.

Every language file holds exactly the same field set:

| Field | Notes |
| --- | --- |
| `ShortDescription` | one sentence, at most 1,000 characters |
| `Description` | the body, at most 10,000 characters, blank line between paragraphs |
| `Feature1` .. `Feature10` | Partner Center adds the bullets - do not type them |

`release-notes/` is the fourth thing, and the only one that is **per submission** rather than durable:
`<version>.en-us.txt`, `<version>.ru.txt`, `<version>.uk.txt` hold the "What's new" for one release,
in the same three languages the notes use on every other surface. The builder above never touches the
`ReleaseNotes` row - the decks it fills outlive a release, and dated prose does not belong in them.
`tools/store/write-release-notes.ps1` writes that one row and nothing else, into
`msix/dist/store-listing-import.csv`:

```powershell
# Partner Center -> Store listings -> Export listing, then:
pwsh -NoProfile -File tools/store/write-release-notes.ps1 `
  -Export ~/Downloads/listingData-9NBTD5SXB8TB-<id>.csv -Version 26.0806.2225
```

Everything a submission needs is assembled in `msix/dist`: the MSIX, this CSV, and the screenshot
payload. Never hand Partner Center a file from the download folder - the export sitting there carries a
BOM the import rejects, and nothing tells you that is why.

Three things are deliberately **not** per language:

- `shared.txt` - `Title` and `CopyrightTrademarkInformation`. They are identifiers, not prose, and
  must read the same in every column.
- `search-terms.<listing-code>.txt` - that language's search terms. A language without such a file
  falls back to `search-terms.txt`, which holds the English set, so a new language ships English terms
  rather than none. Per language: at most seven, no duplicates, nothing forbidden.
  (SP-0034 originally kept one English set in all thirteen columns for reviewability; the owner
  reversed that on 2026-07-27 because Store search matches terms literally and no Hindi or Arabic user
  types "internet radio". The header of `search-terms.txt` records the trade.)
- `forbidden-terms.txt` - terms a search term may never contain. The builder fails on a hit, and now
  checks every language's set rather than one. It carries the non-Latin transliterations of `iptv` and
  the per-market competitor names, because a Latin-only list cannot guard twelve non-English sets.

Anything else in the export (screenshots, logos, trailers, release notes) is left alone. Fill only
what this deck declares; the builder never overwrites a cell that already has content.

The ten files other than `en-us`, `ru` and `uk` are machine-produced and have not been proofread by
a native speaker. Nothing in the copy claims otherwise.
