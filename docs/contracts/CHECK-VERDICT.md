# CHECK-VERDICT - pointer

| | |
| --- | --- |
| **Id** | `CHECK-VERDICT` |
| **Version** | 0.13; CHECK-BASELINE 0.11, CHECK-PLACEMENT 0.11, BUILD-EVIDENCE 0.10, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/automated-checks/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Consumer |

`scripts/test.ps1` reads fresh TRX counters through `TestResultSummary.ps1`: zero/missing/malformed
suites and skips cannot yield PASS; failures take precedence. Its writer emits canonical run/skipped
counts. Build, check, CI and release test entry points use it. `Test-TestResultSummary.ps1` exercises
negative cases. `scripts/check.ps1` aggregates checks, not test counts. The gate scripts print the
contract word `COULD NOT VERIFY` and exit `2` when a prerequisite throws (`Test-CheckVerdictScripts.ps1`
pins this). Still open as owner decisions: the first-failure stop at restore/build, the `Assert-*`
throwers, `smoke-playback.ps1`'s verdict line and the legacy `PASS (N checks)` form. Adoption stays
partial; smoke-playback evidence remains independently mandatory for release.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
