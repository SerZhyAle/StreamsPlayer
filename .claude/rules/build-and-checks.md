---
paths:
  - "build.ps1"
  - "run.ps1"
  - "scripts/**"
  - ".github/workflows/**"
  - "tests/**"
  - "tools/**/*.ps1"
  - "msix/*.ps1"
---

# Build, run and checks

Detail moved verbatim from `CLAUDE.md` and `AGENTS.md` on 2026-11-25; the always-loaded core is `AGENTS.md`. Where the text below says the measured list is in `AGENTS.md` "Fast checks and the release tier", or "recorded here", it is the list in this file.

## Commands

Run from the repository root. A human types these in PowerShell; an **agent must route every `.ps1` through the interpreter** - `pwsh -NoProfile -File ./build.ps1 ..` - because a bare `./build.ps1` in a Bash tool call is refused by the canon's `guard-bash` hook, and a backgrounded one would report exit 0 over a failed build.

**Between releases only the fast tier runs** (SP-0256, canon `TESTING_AND_QA.md` §2): no full build, no test run and no check above 5 s unless the owner names that run. Every command below except `check-fast.ps1` is release tier; a rung skipped for this reason is a `RELEASE` item in the audit, and the report says "not built, not tested until the release". The measured list (check, seconds, date, method) is in `AGENTS.md` "Fast checks and the release tier".

- `pwsh -NoProfile -File ./scripts/check-fast.ps1 [-Area docs|help|site|release-scripts|snapshot]` - the fast tier: every check measured at 5 s or less, run in parallel. Between releases the call is the area of what changed (1.7 to 2.3 s; help 2.2 s with both help checks). `all` read 4.0 s (2026-11-20, STRWIN-0304, 15 checks) and 6.1 to 6.6 s on 2026-11-23 under load (STRWIN-0239 P6 made it 17 checks; every area ran 1.5 to 1.7x its mean the same day, so the machine carried the growth) - re-measure on a quiet machine before calling it inside the cap again; it stays the call when the touched area is unclear. Push CI and `scripts/build-local.ps1` run `all` and nothing else.
- `pwsh -NoProfile -File ./build.ps1 -Test -Deploy:$false` - restore, build, run tests (Debug by default; add `-Configuration Release` to match the release). **`-Deploy` defaults to `$true`**, so a bare `-Test` forces Release *and* deploys to the local machine folders; `-Configuration Debug` then throws.
- `pwsh -NoProfile -File ./run.ps1` - restore, build (Debug), launch the app; it always passes `-Deploy:$false`. `build.ps1 -Run` is *not* the same thing: it deploys first and runs Release.
- `pwsh -NoProfile -File ./scripts/check.ps1` - the release-parity gate: Release restore + build + `dotnet test`, then the documentation gate `scripts/check-docs.ps1` (`DOCUMENT_REGISTRY.jsonl` declares the roots of the maintained corpus and every document under a root is declared there - SP-0287; links, anchors, house style, offline safety - SP-0140) and `tools/site/build-site.ps1 -Check`. `release.yml` runs the same two on the tag's own tree and `pages.yml` on the commit it deploys; push CI runs only the documentation gate, inside `check-fast.ps1`. Run this before proposing a release.
- `pwsh -NoProfile -File ./scripts/smoke-playback.ps1` - the gate `check.ps1` structurally cannot be: it publishes the tree and plays a live radio station (the audio-only LibVLC engine, `StandardAudioPlayback`) and a live video stream (the LibVLC player) through the shipping binary, then runs the console companion `StreamsPlayerCli.exe` from the same folder against its own isolated profile (SP-0277: `commands --json` parses, `status` exits 11 with nothing running and 0 with the launched copy, a start verb exits 10) and keeps what it saw in the verdict's `companion` member. **Green tests are not evidence the product plays** - SP-0093 was a runtime defect underneath our code, 858 tests stayed green, and a release shipped that played nothing. Mandatory before any release (`release.ps1` step 2b), and after any change to media, packaging or a native-shipping dependency.
- `dotnet test StreamsPlayer.sln -c Release --no-build` - run all tests.
- Run one test: `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~CatalogMergerTests"` (all tests live in `StreamsPlayer.Core.Tests`; the App and tools have no tests).
- `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png` - validate the live stream-bank contract against the network.

`dotnet format --verify-no-changes` currently fails on a pre-existing line-ending/encoding baseline; it is **not** a passing gate.

### Build/publish flow caveats

- `build.ps1 -Deploy` (the default) forces Release, publishes the **same self-contained folder a release ships** (native `libvlc\` tree included), and mirrors it into a `StreamsPlayer\` subfolder of each hardcoded local machine folder (`C:\GD\i`, `C:\GD\tc\SZA\_APP`), then checks the deployed `libvlc.dll`/`libvlccore.dll` are byte-identical to the pinned `VideoLAN.LibVLC.Windows` package (SP-0133). The roots are shared with FastMediaSorter LITE, whose own `LibVLCSharp.dll` and `libvlc\` live there - the deploy never writes to the root, except to delete the pre-SP-0133 single-file `StreamsPlayer.exe`. Pass `-Deploy:$false` for an ordinary solution build. This is a local install, **not** a release. It throws if `-Configuration` is bound to non-Release or `-Runtime` to anything but `win-x64`; it does not silently force either.
- Never run `scripts/build-local.ps1` unless the user explicitly requests a commit - it `git add --all` then commits.
- `build.ps1` never releases. This repo's autonomy verdict is **ask first** for every publishing action; the rule it applies has one home in the canon (`RELEASE_AND_DISTRIBUTION.md` §1).

## Fast tier and release tier

- **Fast tier:** `pwsh -NoProfile -File ./scripts/check-fast.ps1 [-Area docs|help|site|release-scripts|snapshot]`
  - the checks below marked fast, in parallel; `all` (the default) takes 4.0 s timed around the call (15 checks ran,
  the snapshot check skipped for want of a built Core; 2026-11-20 after STRWIN-0304 added the help render check,
  seven runs 3.6 to 4.5 s, mean 3.96 s; its own verdict line read 3.3 to 4.2 s), every run inside the 5 s cap, the
  worst 0.5 s inside it. It was 3.64 s over 14 checks (SP-0290, 2026-11-15, twelve runs 3.4 to 3.9 s); the 16-check
  variant with a built Core, snapshot included, is the release's to measure. An area took 2.2 s (help, both help
  checks since STRWIN-0304; 0.9 s with one), 1.7 s (snapshot), 1.9 s (site), 2.3 s (docs) and 1.9 s
  (release-scripts), each the mean of 3 timed around the whole call (2026-11-15; help re-measured 2026-11-20).
  **Between releases call the area
  of what changed**, it is the cheapest. `all` read 6.1 to 6.6 s over three runs on 2026-11-23 (STRWIN-0239 P6
  made it 17 checks; every area was 1.5 to 1.7x its 2026-11-20 mean the same day, so the machine, not the new
  0.56 s check, carried the growth) - re-measure it on a quiet machine before calling it inside the cap again;
  it stays the call when the touched area is unclear. Push CI and `scripts/build-local.ps1` run `all` and nothing else.
- **Release tier:** `pwsh -NoProfile -File ./scripts/check.ps1` (Release restore, build, the suite and every check
  of both tiers), `./scripts/smoke-playback.ps1`, `./msix/build-msix.ps1 -SelfSign` - run by the release checklist
  (`scripts/release.ps1` items 3, 4, 6); `release.yml` builds and tests the tag's own tree and runs the
  documentation gate, the site-sync check and the fixtures push CI no longer carries; `pages.yml` runs the
  documentation gate and the site-sync check on the commit it deploys, after a successful release run or on an
  explicit run, never on push.

Measured list. Method: one direct `pwsh -NoProfile -File <script>` call, wall time around the child process, on
the owner's machine on 2026-10-30, unless the CI step time of run 40234064360 (2026-10-25) or 38010697651
(2026-10-10) is named. A check with no measured time is release tier until a release run measures it; moving a
check into the fast tier needs a time of 5 s or less recorded here first.

| Check | Seconds | Tier |
| --- | --- | --- |
| `scripts/check-docs.ps1` | 1.60 (CI 2-3) | fast |
| `tools/Build-CliDoc.ps1 -Check` | 0.39 (2026-11-14, mean of 3) | fast |
| `tools/Test-CliDoc.ps1` | 1.79 (2026-11-15, mean of 3, SP-0290; 3.03 before) | fast |
| `scripts/check-help.ps1` | 0.47 | fast |
| `scripts/Test-TestResultSummary.ps1` | 0.45 (CI 1) | fast |
| `scripts/Test-ExistingReleaseState.ps1` | 0.40 | fast |
| `scripts/Test-ReleaseVersion.ps1` | 1.31 (2026-11-15, mean of 3, SP-0290; 2.47 before) | fast |
| `msix/Test-MsixIdentity.ps1` | 0.72 (2026-11-12, mean of 3) | fast |
| `scripts/Test-StorePreflightCall.ps1` | 1.17 (2026-11-15, mean of 3, SP-0290; 2.85 before) | fast |
| `scripts/Test-ReleaseLedger.ps1` | 1.16 (2026-11-18, direct call, SP-0292) | fast |
| `scripts/Test-CheckPlacement.ps1` | 0.56 (2026-11-23, mean of 3, STRWIN-0239 P6) | fast |
| `tools/site/Test-HeldAddresses.ps1` | 0.85 | fast |
| `tools/site/Test-ReleaseNotes.ps1` | 0.95 | fast |
| `tools/site/Test-SiteDiscovery.ps1` | 1.40 | fast |
| `tools/site/Test-SiteFacts.ps1` | 0.92 | fast |
| `tools/build-catalog-snapshot.ps1 -Check -Offline` | 1.28 | fast |
| `tools/help/build-help.ps1 -Check` | 1.93 (2026-11-20, mean of 3, STRWIN-0304; 5.37 before) | fast |
| `tools/audit/Test-AuditTools.ps1` | 5.61 | release |
| `tools/store/Test-StoreListing.ps1` | CI 6 | release |
| `scripts/Test-MeasureCaptureTimes.ps1` | 7.66 | release |
| `tools/audit/Test-AuditVerdictGate.ps1` | 10.21 | release |
| `tools/site/build-site.ps1 -Check` | CI 15-20 | release |
| `msix/Test-AssertMsixPackage.ps1` | 21.51 | release |
| `tools/Test-CatalogSnapshot.ps1` | above 30 (stopped) | release |
| `dotnet restore` + `dotnet build StreamsPlayer.sln -c Release` | CI 30 + 30 | release |
| `scripts/test.ps1` (the whole suite; a `--filter` run still builds first) | CI 457 (Test step of release run 44142247477, 2026-11-17, the test projects one at a time with Core.Tests first; 417 before, in parallel) | release |
| `scripts/Test-CheckVerdictScripts.ps1`, `scripts/Test-SmokeVerdictGate.ps1`, `scripts/Test-ReleaseArtifactVersion.ps1`, `msix/Test-AppxManifest.ps1`, `tools/site/make-demo.ps1 -Check`, `scripts/smoke-playback.ps1` | not measured | release |

## Development commands

- `build.ps1` deploys by default: `-Deploy` is `$true` unless you pass `-Deploy:$false`. Deploying forces Release; it *throws* rather than silently coercing if `-Configuration` is bound to non-Release or `-Runtime` to anything but `win-x64`. Every `build.ps1` line below inherits that.
- `pwsh -NoProfile -File ./build.ps1 -Test -Deploy:$false` - restore, build and run tests without touching the local app folders.
- `pwsh -NoProfile -File ./build.ps1 -Deploy` - build a self-contained Release EXE and copy it to the local SZA app folders; this is not a release.
- `pwsh -NoProfile -File ./run.ps1` - restore, build and launch the app in Debug, never deploying. `build.ps1 -Run` deploys first and runs Release.
- `pwsh -NoProfile -File ./scripts/check.ps1` - the release-parity gate: Release restore, build, test, the documentation gate and the site sync check; ends in one `check: PASS|FAIL|COULD NOT VERIFY` line.
- `pwsh -NoProfile -File ./scripts/smoke-playback.ps1` - plays a live radio station and a live video stream through the shipping binary, then drives the console companion `StreamsPlayerCli.exe` from the same folder (`scripts/SmokeCompanionRound.ps1`, SP-0277). Mandatory before a release; `check.ps1` cannot cover it, because it tests this repository's code and SP-0093 broke the runtime beneath it while all 858 tests stayed green.
- `dotnet format StreamsPlayer.sln --verify-no-changes` - formatting diagnostic; it currently reports a pre-existing line-ending/encoding baseline and is not a passing gate until that baseline is normalized.
- `dotnet run --project src/StreamsPlayer.App` - run the desktop application.
- `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png`
  - check the live catalog contract.
- `pwsh -NoProfile -File ./msix/build-msix.ps1 -SelfSign` - build and locally test an MSIX package; use only for package work.
- `pwsh -NoProfile -File ./scripts/release.ps1` - print the manual release checklist only; it changes no remote state.
- `pwsh -NoProfile -File ./scripts/New-ReleaseDossier.ps1` - after a release: gathers its assets, the unsigned Store package (built from the tag in a scratch clone and copied to `msix/dist`), the winget manifests, the verdicts and the What's new texts into `artifacts/release-<version>/`, reads the state of every channel and writes the README of what is left by hand. Changes nothing on a remote.
