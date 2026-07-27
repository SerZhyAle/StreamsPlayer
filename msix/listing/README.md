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

Three things are deliberately **not** per language:

- `shared.txt` - `Title` and `CopyrightTrademarkInformation`. They are identifiers, not prose, and
  must read the same in every column.
- `search-terms.txt` - the seven search terms. The owner's decision for SP-0034 is that they stay
  English in all thirteen columns, so there is one set to check rather than thirteen.
- `forbidden-terms.txt` - terms a search term may never contain. The builder fails on a hit.

Anything else in the export (screenshots, logos, trailers, release notes) is left alone. Fill only
what this deck declares; the builder never overwrites a cell that already has content.

The ten files other than `en-us`, `ru` and `uk` are machine-produced and have not been proofread by
a native speaker. Nothing in the copy claims otherwise.
