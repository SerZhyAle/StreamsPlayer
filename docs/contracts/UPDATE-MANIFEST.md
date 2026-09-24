# UPDATE-MANIFEST - pointer

| | |
| --- | --- |
| **Id** | `UPDATE-MANIFEST` |
| **Version** | 0.9 (draft) |
| **Home** | `Contracts/app-update-feed/README.md` |
| **Owner** | sza.od.ua hub |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Manifest Location:** Update discovery resolves `https://sza.od.ua/updates/streams-player.json` or fallback GitHub release endpoints.
2. **Silent Degradation:** Network failure or unparseable JSON during update check must never block application startup or show modal error popups.
3. **Integrity Check:** Verify SHA-256 binary hash before prompting or executing external installer packages.

Evidence: `scripts/check.ps1`, `installer/StreamsPlayer.iss`, `docs/index.html`.
