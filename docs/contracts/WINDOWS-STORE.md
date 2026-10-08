# WINDOWS-STORE - pointer

| | |
| --- | --- |
| **Id** | `WINDOWS-STORE` |
| **Version** | 0.1 (draft - being drawn; a product supplements it by proposal) |
| **Home** | `Contracts/windows-store/README.md` |
| **Owner** | StreamsPlayer - this repository writes the amendments, in the store, before the code moves |
| **This product's role** | **producer** of the MSIX and of the Partner Center listing import file for Store ID `9NBTD5SXB8TB` |

## What this product hands to the Store

- the **MSIX** built by `msix/build-msix.ps1` (unsigned for the upload, `-SelfSign` for a local test only);
- the **listing import file** `msix/dist/store-listing-import.csv`, built from a fresh Partner Center export by
  `tools/store/write-release-notes.ps1` and then `tools/store/build-store-listing-csv.ps1` (order and commands in
  `msix/listing/README.md`), from the copy decks in `msix/listing/`.

Out of the contract's scope by its section 5 and not read against it: the release order and the winget and GitHub
legs (canon), the certification kit procedure (canon), the version stamp (`PACKAGE-VERSIONING`), the warning text for
an unsigned installer (`INSTALL-TRUST`).

## What this repository must do to stay conformant

The rules are in the home page. Repo-local: which code holds each one, so a change can be routed.

| Rule | Held by |
| --- | --- |
| 1 identity supplied in full | `msix/build-msix.ps1` parameter defaults (`SZA.StreamsPlayer`, the account Publisher, `SZA`), read back out of the packed archive and compared with the reserved constants held in `msix/Assert-MsixPackage.ps1` (SP-0190) |
| 2 unsigned upload | `-SelfSign` is the only signing path and writes `...-selfsigned.msix`; `msix/Assert-MsixPackage.ps1` asserts no `AppxSignature.p7x` in the upload candidate and refuses the self-signed name offered as the upload (SP-0190) |
| 3 remap of the stamp | `msix/build-msix.ps1` (int-cast parts, revision 0, parts at most 65535); the packed version is checked against it and against the MSIX shape by `msix/Assert-MsixPackage.ps1`. The comparison with the dashboard's version stays with the submission |
| 4 notices inside the package | copied to the staging folder by `msix/build-msix.ps1`; asserted as a member of the packed archive by `msix/Assert-MsixPackage.ps1` |
| 6, 7 fresh export, columns by name | `tools/store/build-store-listing-csv.ps1` reads the header row; a language with no deck fails the run |
| 8 encoding | `tools/store/write-release-notes.ps1` (BOM-free, CRLF throughout, refuses a bare LF) and the builder's writer |
| 9 caps checked before writing | `tools/store/build-store-listing-csv.ps1`, in the deck-reading loop (Feature 200, short description 1,000, description 10,000); `write-release-notes.ps1` for the 1,500 of release notes |
| 12 fill-only-empty, explicit replace | the builder's `-ReplaceCopy` and its "Replaced" list |
| 13 round trip | `msix/store-listing-export.sample.csv` (`-text` in `.gitattributes`) and `build-store-listing-csv.ps1 -FillNothing` |
| 14, 15 identifiers, search terms | `msix/listing/shared.txt`, `search-terms.<code>.txt`, `forbidden-terms.txt`, checked by the builder |
| 16 "What's new" | `msix/listing/release-notes/<version>.en-us.txt`, `.ru.txt` and `.uk.txt`; the accumulated Store block lives in `msix/store-listing.md` |

**Conformance evidence.** The contract has no vectors. Rung 1 runs by hand (`-FillNothing`, byte-identical).
Rung 2 has a manual negative run only. Rung 3 is built: `msix/Assert-MsixPackage.ps1` runs at the end of `build-msix.ps1` and fails closed, and `msix/Test-AssertMsixPackage.ps1` (part of `scripts/check.ps1`) pins each refusal. Rung 4 is the owner's confirmation of an accepted import.

**Deviations** are recorded in the store's `_meta/REGISTRY.md`, never here.
