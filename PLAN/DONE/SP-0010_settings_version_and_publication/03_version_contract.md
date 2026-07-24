# Phase 03: Calendar version contract

**Status:** Completed

1. Set the canonical version in `Directory.Build.props`, preserving `YY.MMDD.HHmm` in informational and file metadata.
   - Check: assembly inspection reports the exact informational/file version and a compatible normalized assembly version.
2. Document the version rule in `AGENTS.md` and align `.github/workflows/release.yml`, `scripts/release.ps1`, and `msix/build-msix.ps1`.
   - Check: release tags accept only `vYY.MMDD.HHmm`; MSIX accepts/derives only `YY.MMDD.HHmm.0`; package builds pass the three-part value into application metadata.
