# WINDOWS-STORE - pointer

| | |
| --- | --- |
| **Id** | `WINDOWS-STORE` |
| **Version** | 0.5, 2026-11-15 (SP-0278) |
| **Home** | `Contracts/windows-store/README.md` |
| **Owner** | StreamsPlayer |
| **This product's role** | Owner; producer of listing imports and MSIX |

The listing builder checks the 21-word bound across the seven actual merged search-term cells
before writing; overflow names the language/count and preserves prior output/staging. New imports
use no BOM; FillNothing preserves a BOM-bearing export. Since SP-0282 `Write-ListingCsv` calls
`Assert-ListingCsvDialect` (`tools/store/ListingCsvDialect.ps1`) on the finished text and refuses a BOM nobody asked
for, a bare LF or CR, a trailing line break and an open last cell before anything is written (rule 8, rung 2).
Test-StoreListing.ps1 holds the boundaries and the negative cases; it is release tier, so it runs at the release.
Assert-MsixPackage.ps1 retains identity/signature/notices read-back and its fixture gate.
Local cap/round-trip/package evidence is partial 0.2, not a fresh Partner Center import/re-export.
SP-0239 carries that rung at the next authorized submission; package and update anchors are untouched.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
