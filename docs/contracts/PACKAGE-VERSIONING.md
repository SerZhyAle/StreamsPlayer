# PACKAGE-VERSIONING - pointer

| | |
| --- | --- |
| **Id** | `PACKAGE-VERSIONING` |
| **Version** | 0.5 (draft) |
| **Home** | `Contracts/package-versioning/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Single Instant Timestamp:** The release instant (`yyMMddHHmm`, e.g. `26.0924.1704`) is derived once and pinned across packaging invocations by `scripts/release.ps1` and `build.ps1`.
2. **Artifact Version Parity:** Applied consistently to `StreamsPlayer.exe` PE `FileVersion` and `ProductVersion`, Inno Setup installer (`installer/StreamsPlayer.iss`), and MSIX package identity in `msix/build-msix.ps1`.
3. **No Sentinel In Production:** Development sentinels are never published to GitHub Releases, Microsoft Store, or winget feeds.

Evidence: `scripts/release.ps1`, `build.ps1`, `msix/build-msix.ps1`, `installer/StreamsPlayer.iss`.

**0.5, read 2026-10-07 (SP-0206).** 0.3 registers the StreamsPlayer Android bands (not this repository); 0.4 adds the `dotted-loose`
rendering for FastMediaSorter_Lite; 0.5 adds the independent-clock edition profile (section 8), which does not apply here. This
product's rendering is `dotted` (`v<yy>.<MMdd>.<HHmm>`, `^v\d{2}\.\d{4}\.\d{4}$`, enforced at `.github/workflows/release.yml:53`
and `scripts/release.ps1`); the MSIX remap is `<yy>.<MMdd>.<HHmm without zeros>.0` (`msix/build-msix.ps1:85`), order-preserving
with the 65535 cap checked. Owed and not written: the section 4 fixed-instant vector tests for the tag and the remap.
