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

Release waiver (2026-11-27): the owner may waive `smoke-playback`, `code-audit` and `test-suite` for one named
version through `release-verdicts/<version>.waiver.json` (`scripts/ReleaseWaiver.ps1`). The gate then warns that it
did not run; a recorded verdict is never covered, and a version without the file is gated as before. First used for
`26.1127.1125`, which therefore does not meet BUILD-EVIDENCE rule 8 (dated exception in the catalog registry).

CHECK-PLACEMENT (0.11), since STRWIN-0239 P6 (2026-11-23): `scripts/check-placement.jsonl` holds one judged
record per check (50 records: 19 per-change, 8 release, 4 build, 19 hand-run; the repo has a per-change runner -
ci.yml runs the fast tier on every push - so no "none" declaration is owed), and
`scripts/Test-CheckPlacement.ps1` checks both directions: a per-change record must be a named check of
`check-fast.ps1` (directly, through its wrapper, or by ci.yml itself), a release record must be invoked by
`release.yml`, a build record by every build runner it names; and every `check.ps1` child, every fast-tier check
and every PowerShell script under `scripts/`, `tools/` and `msix/` that emits a verdict line must carry a record.
The detector is textual, so a verdict built without a literal closed word, or a script outside those roots, is a
named blind spot. The checker is itself fast tier (0.56 s, 2026-11-23); its wiring facts were read from the
workflows and the build scripts, not observed in a live CI or release run.

BUILD-EVIDENCE (0.11): rule 1 - since STRWIN-0239 P6 the three aggregator verdict lines name the subject they
judged: `tests: PASS (Release, run=N skipped=M)`, `check: PASS (Release, N checks, M skipped)` and
`check-fast: PASS (<area>, N checks, M skipped, T s)` (`check.ps1`'s shape was verified only through its stub-tree
fixtures, not a live Release run). Rule 8 - the binding this product states is **1, tests in the publishing job**:
`release.yml` builds the artifacts and runs the suite on the same tree, then reads the version back out of what it
built (`Assert-ReleaseArtifactVersion.ps1`, whose subject banner names the version and the artifacts), before the
release is created; a run that finds a complete existing publication skips the rebuild and its guard compares the
published asset hashes instead. The banner change is pinned by `Test-ReleaseArtifactVersion.ps1`, which is release
tier and was not run for this change.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
