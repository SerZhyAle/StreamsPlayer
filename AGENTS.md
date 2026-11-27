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

## Command line

StreamsPlayer has a command line for scripts and agents - read docs/CLI.md; list the commands with `commands --json`; any command above risk `safe` needs the setting 'let other programs ask this app'.

## Read this before touching an area

This file is the core. The detail is in `.claude/rules/`: Claude Code loads a file by its `paths:` list; every other agent (Codex included) opens it **before** the first edit or command in that row. Method documents are in `docs/agent/`.

| Touching | Read first |
| --- | --- |
| build, run, test, a check or its measured time: `build.ps1`, `run.ps1`, `scripts/`, workflows | [build-and-checks.md](.claude/rules/build-and-checks.md) |
| a version, a release, `msix/`, `winget/`, `installer/`, the Store, frozen identities | [release-and-version.md](.claude/rules/release-and-version.md) |
| catalog, merge, persistence, local state: `src/StreamsPlayer.Core`, its tests, the harness | [catalog-data.md](.claude/rules/catalog-data.md) |
| code in `src/` or `tests/`, windows, media playback, localization, help, the CLI companion | [code.md](.claude/rules/code.md) |
| `docs/`, site and help generators, `POSITIONING.md`, CLI reference, `DOCUMENT_REGISTRY.jsonl`, project layout | [docs-and-site.md](.claude/rules/docs-and-site.md) |
| tickets, `/streamsplayer-*` skills, role agents, `memory/` | [agent-workflow.md](.claude/rules/agent-workflow.md) |

- `PLAN/` (tickets) is **untracked** (`.gitignore`): nothing outside this working tree may depend on a ticket's contents.
- Generated, never hand-edited: `docs/*.html` (`tools/site/build-site.ps1`), `docs/help/` (from `help/`), `docs/CLI.md` (from `docs/cli-commands.json`). A new `Window` class owes a row in `help/help-map.json`, and a changed label or control owes its copy, in the same edit.
- Shared contracts have one pointer each in [docs/contracts/](docs/contracts/README.md); the first lines of `CLAUDE.md` name the store. A change to a contract surface is written in the store first and in this code second.

## Fast checks and the release tier

**Between releases nothing above 5 seconds runs** - no full build, no test suite, no check measured above 5 s or
never measured - unless the owner names that run (canon `TESTING_AND_QA.md` §2; SP-0256). A technical piece, a
point fix or a small improvement is checked by static inspection, the fast tier and a direct call of what
changed, and reported as "not built, not tested until the release"; a slower rung it skipped is a `RELEASE` item
in its audit, naming the command below that the release runs.

- **Fast tier:** `pwsh -NoProfile -File ./scripts/check-fast.ps1 [-Area docs|help|site|release-scripts|snapshot]` - between releases call the area of what changed; `all` (the default) when the touched area is unclear.
- **Release tier:** every other build, test and check, among them `scripts/check.ps1` (the release-parity gate), `scripts/smoke-playback.ps1` and `msix/build-msix.ps1 -SelfSign`.
- The measured list is in [build-and-checks.md](.claude/rules/build-and-checks.md): a check enters the fast tier only with a time of 5 s or less recorded there first.
- Every release starts with the contract catch-up ([release-and-version.md](.claude/rules/release-and-version.md)).

## Development commands

Run from the repository root. A human types these in PowerShell. **An agent must prefix every `.ps1`
with `pwsh -NoProfile -File`** - a bare `./build.ps1` in a Bash tool call is refused by the canon's
`guard-bash` hook (`GITHUB_INTERACTION.md` §6), and backgrounding one would report the wrapper's exit
code instead of the build's. Every command below that builds, tests or runs a check of the release tier is for
the release or for a run the owner names (section above).

- `build.ps1` **deploys to the local machine folders by default**: pass `-Deploy:$false` for an ordinary build.
- `pwsh -NoProfile -File ./build.ps1 -Test -Deploy:$false` - restore, build, test. `pwsh -NoProfile -File ./run.ps1` - Debug build and launch, never deploying.
- `dotnet format --verify-no-changes` fails on a pre-existing line-ending/encoding baseline and is **not** a passing gate.
- Green tests are not evidence the product plays: `scripts/smoke-playback.ps1` is mandatory before any release, and a change to media, packaging or a native-shipping dependency owes it.

Never run `scripts/build-local.ps1` unless the user explicitly requests a commit: it runs the fast tier (`scripts/check-fast.ps1`), then `git add --all` and commits.

## Key data-flow contracts

Do not break these without a product decision and updated tests. The bank is the shared `STREAM-BANK` contract: where it and the code disagree, the contract wins and the code is a ticket. Full text: [catalog-data.md](.claude/rules/catalog-data.md).

- Preserve explicit catalog refresh: do not introduce automatic background
  downloads without an explicit product decision.
- Preserve the URL merge contract and the protection of `MANUAL` and `IMPORTED`
  rows. Update tests with any contract change.
- A catalog row the bank stops listing is deleted only when it carries nothing the user made; otherwise it is retired. A new per-channel user value is added to `UserAuthoredChannels` in the same change, or a refresh silently deletes it.

## Code and test conventions

- Keep catalog and delivery rules in `StreamsPlayer.Core`; keep WPF concerns in
  `StreamsPlayer.App`.
- Use nullable reference types and implicit usings already enabled by the
  projects. Follow standard C# naming: PascalCase for types/members,
  camelCase for locals and parameters.
- Build with `dotnet build StreamsPlayer.sln -c Release` and test with
  `dotnet test StreamsPlayer.sln -c Release --no-build` as part of a release
  (`scripts/check.ps1` runs both), never between releases unless the owner
  names that run; between releases run `scripts/check-fast.ps1 -Area <the area of what changed>`.
- `StreamsPlayer.Core` never references WPF, App, Cli, tools or tests.
- The interface languages are declared once (`InterfaceLanguages` in Core) - never restate the list.
- Keep changes scoped. Avoid drive-by formatting and opportunistic refactors. Aim for files below ~500 lines and keep WPF windows focused on UI coordination.
- Do not add raw logging to App or Core until a logging facade is deliberately introduced. `Console.WriteLine` is appropriate in the CatalogHarness.
- Do not introduce trivial comments, broad/empty catches, duplicated values where a constant exists, lifecycle-unsafe async work, live-path stubs, or dead artifacts. Comments explain why, not visible mechanics.
- Store temporary evidence and backups under `temp/`, organized by ticket (`temp/<ticket>/`, or `temp/scratch/` when none), never at the repository root. The legacy `tmp/` tree is historical local evidence referenced by closed tickets; do not rename it, do not add to it. Record checks as `expected: ... | actual: ...`, and rerun the narrowest meaningful check before declaring completion. A changed GUI action needs run-and-observe evidence, not merely a build.
- Update user-facing documentation with user-visible behaviour changes. See `docs/agent/` for the lifecycle, research, quality, validation, memory, cost and code-audit disciplines.

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

- `YY.MMDD.HHmm` in the **author's local release time** (Europe/Malta); the shape is frozen and git tags add a `v`. Versions up to `26.0806.2131` were stamped in UTC.
- A real release takes its version from the `v*` tag, never from a hand edit; it must be later than every published version, and a timestamp is never reused.
- The rest (DST, the MSIX four-part form, the local build stamp): [release-and-version.md](.claude/rules/release-and-version.md).

## Universal Agent Kit workflow

- Communication, autonomy and the evidence rule live in the canon: `AUTHOR.md` "Language" and "Working
  style", `AI_USAGE.md`, `TESTING_AND_QA.md`. They are not restated here.
- Research in this order: `README.md`; relevant `PLAN/` ticket; symbols located with `rg` and their code/tests; official version-specific documentation. Never invent paths, symbols, APIs, or behaviour.
- Open `memory/MEMORY.md` at session start, but verify remembered repository claims against the working tree before acting on them.
- Dependency direction is App UI -> Core; CatalogHarness -> Core; Tests -> Core. Core must remain independent of WPF, App, tools, and tests.
- Use the cheapest sufficient `/streamsplayer-*` rung; the spec pipeline is for changes with real design decisions.
- Ticket IDs use the prefix `STRWIN` (`STRWIN-0001`), files under `PLAN/`; status comes from the working tree, never the filename.

## SZA Unified Rules (canon)

StreamsPlayer follows the portfolio-wide **SZA Unified Rules** for repository layout, documentation, versioning, testing, release, localization, security, and AI usage.

- **Canon home:** the `sza` Claude Code plugin, from `github.com/SerZhyAle/sza-unified-rules`. Consumption
  model is **reference**: the universal rules have one home there and are not restated here. Until
  2026-07-27 this file kept them restated in-repo so the repository stayed self-contained for CI and
  outside contributors - that argument is void now the canon installs as a plugin and travels with the
  session, and the restatement had already drifted (see the 2026-07-26 drift correction in the contrib
  record). Adoption is stamped in `.sza-canon.json`.
- **This repo's record** lives in the canon at `contrib/streams_player.md` - the verified overlay facts, channel-matrix rows, and the legitimate divergences (DIVERGE deltas) specific to StreamsPlayer.
- **Frozen anchors, never changed:** winget `PackageIdentifier`, the MSIX Identity `Name`/`Publisher`, and the Inno `AppId` `{15F4F08C-E78B-41B7-9039-6A3332D7D080}` (a new one installs a second parallel copy). Overlay and coupling shape: [release-and-version.md](.claude/rules/release-and-version.md).
- **Editing the canon:** universal-rule fixes land in the canon first, then spread back here. Never edit the canon from a StreamsPlayer session except its own `contrib/streams_player.md` record.
