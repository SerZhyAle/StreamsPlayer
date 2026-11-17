# CHECK-VERDICT - pointer

| | |
| --- | --- |
| **Id** | `CHECK-VERDICT` |
| **Version** | 0.14; CHECK-BASELINE 0.12, CHECK-PLACEMENT 0.11, BUILD-EVIDENCE 0.11, 2026-11-15 (SP-0278) |
| **Home** | `Contracts/automated-checks/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Consumer |

`scripts/test.ps1` reads fresh TRX counters through `TestResultSummary.ps1`: zero/missing/malformed
suites and skips cannot yield PASS; failures take precedence. Its writer emits canonical run/skipped
counts. Build, check and release test entry points use it (push CI runs no tests since SP-0256). `Test-TestResultSummary.ps1` exercises
negative cases. `scripts/check.ps1` aggregates checks, not test counts. The gate scripts print the
contract word `COULD NOT VERIFY` and exit `2` when a prerequisite throws (`Test-CheckVerdictScripts.ps1`
pins this). Since SP-0282 the sixteen self-tests and the two operation proofs (`Assert-MsixPackage.ps1`,
`Assert-ReleaseArtifactVersion.ps1`) print `PASS (run=N skipped=0)` (item A.1), and `Test-CheckVerdictScripts.ps1`
holds that spelling statically; the form of the tokens, bare as `TestResultSummary.ps1` prints them or inside the
parenthesis, is the catalog's question (`automated-checks/PROPOSAL-2026-11-15-streamsplayer-bare-verdict-tokens.md`).
`scripts/check-help.ps1` holds `help/help-baseline.txt` against its copy at `HEAD` and names a line the working file
added (CHECK-BASELINE item B); without git or a committed copy it says so on its verdict line. Still open as owner
decisions: the first-failure stop at restore/build, the `Assert-*` throwers' other lines and `smoke-playback.ps1`'s
verdict line. Adoption stays partial; smoke-playback evidence remains independently mandatory for release. None of
this was run to the release tier: the registry rows move after the release suite.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
