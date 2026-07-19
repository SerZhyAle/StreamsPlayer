# Repository Guidelines

## Ownership and publishing boundary

StreamsPlayer is an independent Windows product owned and authored by **Serhii
Zhyhunenko** (`SerZhyAle`, `serzhyale@gmail.com`). Its public home is intended
to be `https://github.com/SerZhyAle/StreamsPlayer`, with GitHub Pages at
`https://serzhyale.github.io/StreamsPlayer/`.

It consumes the published FastMediaSorter stream bank as an external data
contract. Do not copy FastMediaSorter application code, change that repository,
or turn a StreamsPlayer feature into a FastMediaSorter feature.

Never push a tag, create a GitHub release, submit winget manifests, upload to
Partner Center, or publish Pages unless the user explicitly asks for a release
or publication. A local build is not a release.

## Project layout

- `src/StreamsPlayer.Core` — platform-neutral catalog contracts, parsing, merge
  and persistence.
- `src/StreamsPlayer.App` — WPF Windows desktop application.
- `tests/StreamsPlayer.Core.Tests` — unit and contract tests.
- `tools/StreamsPlayer.CatalogHarness` — live-bank diagnostic harness.
- `docs/specifications/streams.txt` — standalone product specification.
- `docs/agent/` — agent workflow and validation guidance.
- `docs/` and `assets/` — GitHub Pages and product documentation assets.
- `.github/` — CI, release automation, and contribution templates.
- `msix/` — Store-ready package template and package-build guidance.
- `winget/` — release-manifest templates and submission notes.

## Development commands

Run from the repository root in PowerShell.

- `./build.ps1 -Test` — restore, build and run tests.
- `./build.ps1 -Deploy` — build a self-contained Release EXE and copy it to the local SZA app folders; this is not a release.
- `./build.ps1 -Run` or `./run.ps1` — restore, build and launch the app.
- `./scripts/check.ps1` — Release restore, build, and test check.
- `dotnet format StreamsPlayer.sln --verify-no-changes` — formatting diagnostic; it currently reports a pre-existing line-ending/encoding baseline and is not a passing gate until that baseline is normalized.
- `dotnet run --project src/StreamsPlayer.App` — run the desktop application.
- `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png`
  — check the live catalog contract.
- `./msix/build-msix.ps1 -SelfSign` — build and locally test an MSIX package; use only for package work.
- `./scripts/release.ps1` — print the manual release checklist only.

Never run `./scripts/build-local.ps1` unless the user explicitly requests a commit: it stages and commits changes.

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

Use `main` as the primary branch. Commit subjects are short and imperative,
for example `Add Store package template` or `Fix catalog URL merge`. Keep each
commit focused. Pull requests should state the user-visible result, verification
commands, linked issues where relevant, and screenshots for WPF, Store, or web
page changes.

Configure this repository only (not the user's global Git identity):

```powershell
git config user.name "Serhii Zhyhunenko"
git config user.email "serzhyale@gmail.com"
git config pull.rebase false
git config init.defaultBranch main
```

After the GitHub repository is created, add the remote:

```powershell
git remote add origin https://github.com/SerZhyAle/StreamsPlayer.git
git push -u origin main
```

## Version convention

- StreamsPlayer versions use UTC release time in `YY.MMDD.HHmm` form, for example `26.0719.0131`.
- Git tags use the same value with a `v` prefix: `v26.0719.0131`.
- `Version`, `AssemblyVersion`, `FileVersion`, and `InformationalVersion` in `Directory.Build.props` are updated together before a release. The Settings window displays `InformationalVersion`.
- MSIX package identity requires four components, so it appends only `.0`: `26.0719.0131.0`. Winget and GitHub retain the canonical three-component value.
- A release version must be later than every published version. Do not reuse a timestamp for different package contents.

## Universal Agent Kit workflow

- Chat, code, documentation, logs, and commits are English. Be concise, technical, and evidence-led.
- Read, search, build, test, and inspect the working tree without asking. Raise genuine product, data, architecture, or destructive-action decisions early.
- Research in this order: `README.md`; relevant `PLAN/` ticket; symbols located with `rg` and their code/tests; official version-specific documentation. Never invent paths, symbols, APIs, or behaviour.
- The working tree is the authority for current state. Open `memory/MEMORY.md` at session start, but verify remembered repository claims before acting on them.
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
- Store temporary evidence and backups under `tmp/`, never at the repository root. Record checks as `expected: ... | actual: ...`, and rerun the narrowest meaningful check before declaring completion. A changed GUI action needs run-and-observe evidence, not merely a build.
- Update user-facing documentation with user-visible behaviour changes. See `docs/agent/` for the lifecycle, research, quality, validation, memory, and cost disciplines.
