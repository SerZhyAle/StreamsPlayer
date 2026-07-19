# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

StreamsPlayer is a Windows desktop application (.NET 10, WPF) for internet radio, live video, and RTSP. It is an independent product owned by Serhii Zhyhunenko (`SerZhyAle`). `AGENTS.md` is the authoritative contributor guide; this file summarizes what is not obvious from the code.

## Commands

Run from the repository root in PowerShell.

- `./build.ps1 -Test` — restore, build, run tests (Debug by default; add `-Configuration Release` to match CI).
- `./build.ps1 -Run` or `./run.ps1` — restore, build, launch the app.
- `./scripts/check.ps1` — the release-parity gate: Release restore + build + `dotnet test`. Run this before proposing a release.
- `dotnet test StreamsPlayer.sln -c Release --no-build` — run all tests.
- Run one test: `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~CatalogMergerTests"` (all tests live in `StreamsPlayer.Core.Tests`; the App and tools have no tests).
- `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png` — validate the live stream-bank contract against the network.

`dotnet format --verify-no-changes` currently fails on a pre-existing line-ending/encoding baseline; it is **not** a passing gate.

### Build/publish flow caveats

- `./build.ps1 -Deploy` (default `-Deploy:$true`) forces Release + win-x64, publishes a **self-contained single-file EXE**, and copies it into hardcoded local machine folders (`C:\GD\i`, `C:\GD\tc\SZA\_APP`). Pass `-Deploy:$false` for an ordinary solution build. This is a local install, **not** a release.
- Never run `./scripts/build-local.ps1` unless the user explicitly requests a commit — it stages and commits.
- A local build is never a release. Do not push tags, cut GitHub releases, submit winget/MSIX manifests, or publish Pages unless the user explicitly asks.

## Architecture

Three projects with a strict one-way dependency graph — **Core must never reference WPF, App, tools, or tests**:

```
StreamsPlayer.App (WPF UI) ─┐
StreamsPlayer.CatalogHarness ┤──► StreamsPlayer.Core (platform-neutral)
StreamsPlayer.Core.Tests ────┘
```

- **`src/StreamsPlayer.Core`** — all catalog contracts, parsing, merge, and persistence. Pure .NET, no UI. Key pieces: [Models.cs](src/StreamsPlayer.Core/Models.cs) (records + enums, including CLI arg parsing in `StreamLaunchRequest.Parse`), [StreamBankReader.cs](src/StreamsPlayer.Core/StreamBankReader.cs) → [StreamCatalogCsvParser.cs](src/StreamsPlayer.Core/StreamCatalogCsvParser.cs) (RFC-4180 CSV), [CatalogMerger.cs](src/StreamsPlayer.Core/CatalogMerger.cs), [StreamCatalogStore.cs](src/StreamsPlayer.Core/StreamCatalogStore.cs), and [StreamCatalogService.cs](src/StreamsPlayer.Core/StreamCatalogService.cs) (network refresh orchestration).
- **`src/StreamsPlayer.App`** — WPF app (`AssemblyName` = `StreamsPlayer`). Windows are `MainWindow`, `PlayerWindow`, `SettingsWindow`, `AddStreamWindow`. `MainWindow` is split into partial-class files by concern (`MainWindow.Launch.cs`, `.Settings.cs`, `.Previews.cs`, `.BrowsingSession.cs`, `.Localization.cs`). Grid preview capture and player video use LibVLC.
- **`tools/StreamsPlayer.CatalogHarness`** — console diagnostic that exercises the live catalog contract. `Console.WriteLine` logging is acceptable here only.

### Key data-flow contracts (do not break without a product decision + updated tests)

- **Catalog source is a published external contract**: a ZIP at `StreamCatalogService.CatalogUrl` (FastMediaSorter release). `streams.csv` **must be the first ZIP entry**; the reader rejects the bank otherwise. Optional `favicon-atlas.png` (≤4 MB).
- **Merge protects user data**: `CatalogMerger.Merge` keys channels by URL. It only updates/removes rows whose `SourceOrigin == Catalog`. `MANUAL` and `IMPORTED` (`SourceOrigin.Manual`/`Imported`) rows are never touched by a refresh.
- **Refresh is explicit only**: there are no automatic background catalog downloads. Do not add any.
- **Local state** lives at `%LOCALAPPDATA%\StreamsPlayer` — `catalog-state.json` (written atomically via temp file + move), favicon atlas PNGs, and the session `Current.log`. Persisted via `CatalogState` (a record; JSON with `JsonStringEnumConverter`).

### Media playback

Audio uses WPF `MediaElement`. Video/RTSP and grid preview capture use bundled LibVLC (`LibVLCSharp`, `VideoLAN.LibVLC.Windows`) with a ~10s live buffer. The Core library has no media dependency.

## Conventions

- Nullable reference types and implicit usings are on. PascalCase types/members, camelCase locals. Keep files under ~500 lines; WPF windows coordinate UI only.
- No raw logging facade in App/Core beyond the existing `CurrentLog` (`App.xaml.cs` wires it to unhandled-exception handlers). Don't add ad-hoc `Console.WriteLine` to App or Core.
- Localization is English + Russian via `Localization.en.xaml` / `Localization.ru.xaml` resource dictionaries; the choice persists in `CatalogState.Language`.
- Versioning: `YY.MMDD.HHmm` (UTC), set in [Directory.Build.props](Directory.Build.props). A release version must exceed every published version; never reuse a timestamp.

## Workflow tooling

This repo runs the Universal Agent Kit method. **`AGENTS.md` is the authoritative rules file** — read it and the applicable `docs/agent/` document before non-trivial work. The method in one line: research before acting, split *what/why* (`/streamsplayer-spec`) from *how* (`/streamsplayer-spec-tech`), plan in verifiable phases, stay cheap when the task is small, and prove "done" with evidence, not a green build. A changed GUI action needs run-and-observe evidence.

- **Skills.** The same procedures are available three ways: native Claude Code slash commands in [.claude/commands/](.claude/commands/) (`/streamsplayer-quick`, `-fix`, `-research`, `-spec`, `-spec-tech`, `-spec-dev`, `-spec-check`, `-spec-fix`, `-spec-all`, `-backlog`, `-park`, `-ui-clarify`, `-verify`, `-review`, `-git`, `-caveman[-commit|-review]`), Codex/`$`-invoked skills in [.agents/skills/](.agents/skills/), and the shared `SKILL.md` bodies both point to. The `.claude/commands/*` files are thin wrappers — the procedure lives in `.agents/skills/streamsplayer-*/SKILL.md`.
- **Agents.** Role subagents in [.claude/agents/](.claude/agents/) (`streamsplayer-rd-lead` is the default orchestrator, plus `-solution-researcher`, `-implementer`, `-doc-writer`), mirrored from [.codex/agents/](.codex/agents/).
- **Method docs.** [docs/agent/](docs/agent/): `SPEC_LIFECYCLE`, `CODE_QUALITY`, `VALIDATION`, `RESEARCH_INDEX`, `AGENT_MEMORY`, `COST`.
- **Tickets.** Spec-driven planning under [PLAN/](PLAN/): `SP-NNNN` ids, states `Draft → Approved → Tactical → In Progress → Implemented → Verified` (+ `Partial`/`Broken`/`Block*`). Status comes from the working tree, never the filename. Verified strategic tickets and their tactical folders move to `PLAN/DONE/`.
- **Memory.** File-based, committed, shared across tools: [memory/MEMORY.md](memory/MEMORY.md) is the always-loaded index (types: `user`, `feedback`, `project`, `reference`); discipline in `docs/agent/AGENT_MEMORY.md`.
