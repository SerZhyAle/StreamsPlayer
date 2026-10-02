# CHECK-VERDICT - pointer

| | |
| --- | --- |
| **Id** | `CHECK-VERDICT` (with `CHECK-BASELINE`, `CHECK-PLACEMENT`, `BUILD-EVIDENCE`) |
| **Version** | 0.11 / 0.10 / 0.11 / 0.10 (draft) |
| **Home** | `Contracts/automated-checks/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Deterministic Exit Codes:** Quality scripts (`scripts/check.ps1`, `scripts/smoke-playback.ps1`, site builder `-Check`) return standard exit codes (0 for pass, non-zero for failure).
2. **Build Evidence & Monotonic Quality:** Tests run under Release configuration in CI; regressions are prevented by CI gates and conformance unit suites.
3. **Smoke Playback Gate:** `scripts/smoke-playback.ps1` executes live stream validation before any release packaging.

Evidence: `scripts/check.ps1`, `scripts/smoke-playback.ps1`, `build.ps1`, `tests/StreamsPlayer.Core.Tests/DesktopUxConformanceTests.cs`.
