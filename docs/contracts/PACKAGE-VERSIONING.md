# PACKAGE-VERSIONING - pointer

| | |
| --- | --- |
| **Id** | `PACKAGE-VERSIONING` |
| **Version** | 0.1 (draft) |
| **Home** | `Contracts/package-versioning/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Single Instant Timestamp:** The release instant (`yyMMddHHmm`, e.g. `26.0924.1704`) is derived once and pinned across packaging invocations by `scripts/release.ps1` and `build.ps1`.
2. **Artifact Version Parity:** Applied consistently to `StreamsPlayer.exe` PE `FileVersion` and `ProductVersion`, Inno Setup installer (`installer/StreamsPlayer.iss`), and MSIX package identity in `msix/build-msix.ps1`.
3. **No Sentinel In Production:** Development sentinels are never published to GitHub Releases, Microsoft Store, or winget feeds.

Evidence: `scripts/release.ps1`, `build.ps1`, `msix/build-msix.ps1`, `installer/StreamsPlayer.iss`.
