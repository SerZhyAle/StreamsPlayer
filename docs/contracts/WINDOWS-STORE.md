# WINDOWS-STORE - pointer

| | |
| --- | --- |
| **Id** | `WINDOWS-STORE` |
| **Version** | 0.2, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/windows-store/README.md` |
| **Owner** | StreamsPlayer |
| **This product's role** | Owner; producer of listing imports and MSIX |

The listing builder checks the 21-word bound across the seven actual merged search-term cells
before writing; overflow names the language/count and preserves prior output/staging. New imports
use no BOM; FillNothing preserves a BOM-bearing export. Test-StoreListing.ps1 holds the boundaries.
Assert-MsixPackage.ps1 retains identity/signature/notices read-back and its fixture gate.
Local cap/round-trip/package evidence is partial 0.2, not a fresh Partner Center import/re-export.
SP-0239 carries that rung at the next authorized submission; package and update anchors are untouched.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
