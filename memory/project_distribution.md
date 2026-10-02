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
  - Releases must follow a two-step push: tag and release workflow first, site docs update second to ensure download links never point to missing artifacts.
- **`build.ps1` defaults:**
  - `-Deploy` defaults to `$true`, building a self-contained Release EXE into local SZA app folders. Use `-Deploy:$false` for clean test runs.
