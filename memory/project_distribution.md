---
name: project_distribution
description: Distribution channels (Inno, ZIP, winget, MSIX), version stamp formatting, Partner Center rules, and release sequencing
type: project
---

# Distribution, Packaging & Store Listings

- **Four distribution channels & payload parity (SP-0092, SP-0105):**
  - Inno Setup per-user EXE, portable ZIP, winget manifest (nested portable ZIP), and Microsoft Store MSIX.
  - Inno and ZIP are built from the exact same publish staging directory in CI, ensuring 100% byte/payload parity.
- **Version stamp formatting (PACKAGE-VERSIONING):**
  - Canonical format is `YY.MMDD.HHmm` based on author local time.
  - Windows numeric version resources (MSIX identity, Inno `VersionInfoVersion`) require int-cast components to prevent octal parsing of leading zeroes (`26.0820.1828` -> `26.820.1828.0`).
- **Microsoft Partner Center listing rules (SP-0034, SP-0092):**
  - CSV listings must use CRLF endings and valid UTF-8 BOM.
  - Import is all-or-nothing per language: an error in one language drops that entire locale silently while importing others. Always re-export before importing.
- **Release sequencing & Pages deployment:**
  - Pages deploys only after a successful release run (or an explicit `gh workflow run pages.yml`), never on push, so the site and the release can share one commit; the older two-step push is retired (SP-0256, 2026-10-30).
- **`build.ps1` defaults:**
  - `-Deploy` defaults to `$true`, building a self-contained Release EXE into local SZA app folders. Use `-Deploy:$false` for clean test runs.
- **The Store certification notes are already near a size Partner Center bounds (SP-0276, 2026-11-14):** the `Certification notes` block of `msix/store-listing.md` measured about 1930 characters including its fence; the limit is Partner Center's and is not written down in this repository (about 2000 is what I recall - verify before relying on it). New facts for a reviewer (the command line, `StreamsPlayerCli.exe`) went to the `Privacy and age-rating declarations` bullets, which are not pasted into a bounded field; measure the pasted blocks before appending to them.
