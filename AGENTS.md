# Repository Guidelines

## Ownership and publishing boundary

StreamsPlayer is an independent Windows product owned and authored by **Serhii
Zhyhunenko** (`SerZhyAle`, `serzhyale@gmail.com`). Its public home is intended
to be `https://github.com/SerZhyAle/StreamsPlayer`, with GitHub Pages at
`https://serzhyale.github.io/StreamsPlayer/`.

It consumes the published FastMediaSorter stream bank as an external data
contract. Do not copy FastMediaSorter application code, change that repository,
or turn a StreamsPlayer feature into a FastMediaSorter feature.

Publishing boundary: this repo's autonomy verdict is **ask first** - never push a tag, create a GitHub
release, submit winget manifests, upload to Partner Center, or publish Pages unless the user explicitly
asks. The build-versus-release rule itself has one home in the canon
(`RELEASE_AND_DISTRIBUTION.md` §1); only this repo's verdict is local.

## Project layout

- `src/StreamsPlayer.Core` - platform-neutral catalog contracts, parsing, merge
  and persistence.
- `src/StreamsPlayer.App` - WPF Windows desktop application.
- `tests/StreamsPlayer.Core.Tests` - unit and contract tests.
- `tools/StreamsPlayer.CatalogHarness` - live-bank diagnostic harness.
- `tools/InterfaceLanguages.ps1` - the single shipped-language list, read from the built assembly and dot-sourced by the site and Store tooling.
- `tools/site/` and `tools/store/` - generators for the GitHub Pages site and the Store listing/screenshot pipeline.
- `scripts/` - the release-parity check, the release checklist, and the commit-and-build helper.
- `docs/specifications/streams.txt` - standalone product specification.
- `docs/agent/` - agent workflow and validation guidance.
- `docs/` and `assets/` - GitHub Pages and product documentation assets.
- `memory/` - the committed agent memory index.
- `.github/` - CI, release automation, and contribution templates.
- `msix/` - Store-ready package template and package-build guidance.
- `winget/` - release-manifest templates and submission notes; `manifests/` holds a version-pinned copy of a submitted manifest set and is not the template source.
- `PLAN/` - spec tickets. **Untracked** (`.gitignore`): a fresh clone, CI, or an outside contributor will not have it, so nothing outside this working tree may depend on a ticket's contents.

## Development commands

Run from the repository root. A human types these in PowerShell. **An agent must prefix every `.ps1`
with `pwsh -NoProfile -File`** - a bare `./build.ps1` in a Bash tool call is refused by the canon's
`guard-bash` hook (`GITHUB_INTERACTION.md` §6), and backgrounding one would report the wrapper's exit
code instead of the build's.

- `build.ps1` deploys by default: `-Deploy` is `$true` unless you pass `-Deploy:$false`. Deploying forces Release; it *throws* rather than silently coercing if `-Configuration` is bound to non-Release or `-Runtime` to anything but `win-x64`. Every `build.ps1` line below inherits that.
- `pwsh -NoProfile -File ./build.ps1 -Test -Deploy:$false` - restore, build and run tests without touching the local app folders.
- `pwsh -NoProfile -File ./build.ps1 -Deploy` - build a self-contained Release EXE and copy it to the local SZA app folders; this is not a release.
- `pwsh -NoProfile -File ./run.ps1` - restore, build and launch the app in Debug, never deploying. `build.ps1 -Run` deploys first and runs Release.
- `pwsh -NoProfile -File ./scripts/check.ps1` - Release restore, build, and test check.
- `pwsh -NoProfile -File ./scripts/smoke-playback.ps1` - plays a live radio station and a live video stream through the shipping binary. Mandatory before a release; `check.ps1` cannot cover it, because it tests this repository's code and SP-0093 broke the runtime beneath it while all 858 tests stayed green.
- `dotnet format StreamsPlayer.sln --verify-no-changes` - formatting diagnostic; it currently reports a pre-existing line-ending/encoding baseline and is not a passing gate until that baseline is normalized.
- `dotnet run --project src/StreamsPlayer.App` - run the desktop application.
- `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png`
  - check the live catalog contract.
- `pwsh -NoProfile -File ./msix/build-msix.ps1 -SelfSign` - build and locally test an MSIX package; use only for package work.
- `pwsh -NoProfile -File ./scripts/release.ps1` - print the manual release checklist only; it changes no remote state.
- `pwsh -NoProfile -File ./tools/site/build-site.ps1 -Check` - fail if `docs/` is stale against the generator, writing nothing.

Never run `scripts/build-local.ps1` unless the user explicitly requests a commit: it runs `git add --all` and commits.

## Code and test conventions

- Keep catalog and delivery rules in `StreamsPlayer.Core`; keep WPF concerns in
  `StreamsPlayer.App`.
- Preserve explicit catalog refresh: do not introduce automatic background
  downloads without an explicit product decision.
- Preserve the URL merge contract and the protection of `MANUAL` and `IMPORTED`
  rows. Update tests with any contract change.
- Use nullable reference types and implicit usings already enabled by the
  projects. Follow standard C# naming: PascalCase for types/members,
  camelCase for locals and parameters.
- Build with `dotnet build StreamsPlayer.sln -c Release` and test with
  `dotnet test StreamsPlayer.sln -c Release --no-build` before proposing a
  release.

## Git conventions

Commit and PR discipline - when to commit, message shape, the co-author trailer, English artifacts -
has one home in the canon (`GITHUB_INTERACTION.md` §2-3). Repo-local deltas only:

- The primary branch is `main`, and `origin` is already set to `https://github.com/SerZhyAle/StreamsPlayer.git`.
- A PR touching WPF, the Store listing, or the Pages site carries **screenshots**; every PR carries the
  verification commands it actually ran.
- Identity is configured per repository, never on the user's global Git config:

```powershell
git config user.name "Serhii Zhyhunenko"
git config user.email "serzhyale@gmail.com"
git config pull.rebase false
```

## Version convention

- StreamsPlayer versions use the **author's local release time** (Europe/Malta) in `YY.MMDD.HHmm` form,
  for example `26.0719.0131`. The version's purpose is to tell the owner when a build was made, read
  against the clock on his wall; a stamp he has to convert does not serve that purpose. Versions up to
  and including `26.0806.2131` were stamped in UTC, so they read two hours (one in winter) earlier than
  the local time they were built at. The **shape** `YY.MMDD.HHmm` is frozen for the product's life and
  does not change with this.
- Accepted consequence: the autumn clock change repeats one local hour, so a release inside that hour
  could carry a version lower than one already published. Deliberate - releases are not cut on the hour
  boundary of a DST change, and the monotonicity check before a release is the real guard.
- Git tags use the same value with a `v` prefix: `v26.0719.0131`.
- **For a real release the version is not hand-edited.** `.github/workflows/release.yml` derives it from the `v*` tag (regex + a real `ParseExact`, so an impossible stamp fails the job) and passes it as `-p:Version=/-p:AssemblyVersion=/-p:FileVersion=/-p:InformationalVersion=`. The four fields in `Directory.Build.props` are the *local build* stamp only; the tag wins for anything published. The Settings window displays `InformationalVersion`.
- MSIX package identity requires four components and its version schema forbids leading zeros, so `msix/build-msix.ps1` int-casts each component and appends `.0`: `26.0719.0131` → `26.719.131.0` (with a per-component `≤ 65535` ceiling guard). Winget and GitHub retain the canonical zero-padded three-component value.
- A release version must be later than every published version. Do not reuse a timestamp for different package contents.

## Universal Agent Kit workflow

- Communication, autonomy and the evidence rule live in the canon: `AUTHOR.md` "Language" and "Working
  style", `AI_USAGE.md`, `TESTING_AND_QA.md`. They are not restated here.
- Research in this order: `README.md`; relevant `PLAN/` ticket; symbols located with `rg` and their code/tests; official version-specific documentation. Never invent paths, symbols, APIs, or behaviour.
- Open `memory/MEMORY.md` at session start, but verify remembered repository claims against the working tree before acting on them.
- Dependency direction is App UI -> Core; CatalogHarness -> Core; Tests -> Core. Core must remain independent of WPF, App, tools, and tests.

### Skill routing

- `$streamsplayer-quick`: one trivial deterministic edit; `$streamsplayer-fix`: narrow, understood behaviour bug.
- `$streamsplayer-research`: evidence-first investigation before non-trivial work.
- `$streamsplayer-spec` -> `$streamsplayer-spec-tech` -> `$streamsplayer-spec-dev` -> `$streamsplayer-spec-check`: changes with real design decisions.
- `$streamsplayer-spec-fix`, `$streamsplayer-spec-all`, `$streamsplayer-ui-clarify`, `$streamsplayer-verify`, `$streamsplayer-review`, `$streamsplayer-git`, `$streamsplayer-park`, `$streamsplayer-backlog`, and `streamsplayer-caveman*` follow their named procedures.

### Specifications, quality, and validation

- Ticket IDs use `SP-0001`, `SP-0002`, and so on. Strategic tickets are `PLAN/SP-0001_slug.md`; tactical plans live under `PLAN/SP-0001_slug/`.
- Status comes from reality: `Draft -> Approved -> Tactical -> In Progress -> Implemented -> Verified`, with `Partial`, `Broken`, `Archived`, and documented `Block*` states. Strategic specs contain what/why; tactical plans contain dependency-ordered implementation steps with static checks.
- Keep changes scoped. Avoid drive-by formatting and opportunistic refactors. Aim for files below ~500 lines and keep WPF windows focused on UI coordination.
- Do not add raw logging to App or Core until a logging facade is deliberately introduced. `Console.WriteLine` is appropriate in the CatalogHarness.
- Do not introduce trivial comments, broad/empty catches, duplicated values where a constant exists, lifecycle-unsafe async work, live-path stubs, or dead artifacts. Comments explain why, not visible mechanics.
- Store temporary evidence and backups under `temp/`, organized by ticket (`temp/<ticket>/`, or `temp/scratch/` when none), never at the repository root. The legacy `tmp/` tree is historical local evidence referenced by closed tickets; do not rename it, do not add to it. Record checks as `expected: ... | actual: ...`, and rerun the narrowest meaningful check before declaring completion. A changed GUI action needs run-and-observe evidence, not merely a build.
- Update user-facing documentation with user-visible behaviour changes. See `docs/agent/` for the lifecycle, research, quality, validation, memory, and cost disciplines.

## SZA Unified Rules (canon)

StreamsPlayer follows the portfolio-wide **SZA Unified Rules** for repository layout, documentation, versioning, testing, release, localization, security, and AI usage.

- **Canon home:** the `sza` Claude Code plugin, from `github.com/SerZhyAle/sza-unified-rules`. Consumption
  model is **reference**: the universal rules have one home there and are not restated here. Until
  2026-07-27 this file kept them restated in-repo so the repository stayed self-contained for CI and
  outside contributors - that argument is void now the canon installs as a plugin and travels with the
  session, and the restatement had already drifted (see the 2026-07-26 drift correction in the contrib
  record). Adoption is stamped in `.sza-canon.json`.
- **This repo's record** lives in the canon at `contrib/streams_player.md` - the verified overlay facts, channel-matrix rows, and the legitimate divergences (DIVERGE deltas) specific to StreamsPlayer.
- **Overlay shape:** Windows-desktop, *installer variant* (SP-0092, changed 2026-08-21 from the no-installer variant) - an Inno Setup per-user setup EXE and a portable ZIP in the same GitHub Release, plus winget-portable and Store MSIX. The installer and the archive are compiled from one publish, so their payloads never differ. winget deliberately stays on the ZIP (`InstallerType: zip` + `NestedInstallerType: portable`); moving it to `inno` is a separate submission. Frozen anchors: winget `PackageIdentifier`, the MSIX Identity `Name`/`Publisher`, and the Inno `AppId` `{15F4F08C-E78B-41B7-9039-6A3332D7D080}` - changing the last one does not upgrade an existing install, it creates a second parallel one on every machine that already has it.
- **Coupling shape:** *consumed published release artifact* - StreamsPlayer depends at runtime on another SZA product's release output (the FastMediaSorter catalog ZIP at `StreamCatalogService.CatalogUrl`). This is neither a wire/config contract nor an edition/parity relationship; the merge protects user-owned `MANUAL`/`IMPORTED` rows and refresh is explicit-only.
- **Editing the canon:** universal-rule fixes land in the canon first, then spread back here. Never edit the canon from a StreamsPlayer session except its own `contrib/streams_player.md` record.
