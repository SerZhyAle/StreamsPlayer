# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

StreamsPlayer is a Windows desktop application (.NET 10, WPF) for internet radio, live video, and RTSP. It is an independent product owned by Serhii Zhyhunenko (`SerZhyAle`). `AGENTS.md` is the authoritative contributor guide; this file summarizes what is not obvious from the code.

## Commands

Run from the repository root in PowerShell.

- `./build.ps1 -Test -Deploy:$false` - restore, build, run tests (Debug by default; add `-Configuration Release` to match CI). **`-Deploy` defaults to `$true`**, so a bare `./build.ps1 -Test` forces Release *and* deploys to the local machine folders; `-Configuration Debug` then throws.
- `./run.ps1` - restore, build (Debug), launch the app; it always passes `-Deploy:$false`. `./build.ps1 -Run` is *not* the same thing: it deploys first and runs Release.
- `./scripts/check.ps1` - the release-parity gate: Release restore + build + `dotnet test`. Run this before proposing a release.
- `dotnet test StreamsPlayer.sln -c Release --no-build` - run all tests.
- Run one test: `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~CatalogMergerTests"` (all tests live in `StreamsPlayer.Core.Tests`; the App and tools have no tests).
- `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png` - validate the live stream-bank contract against the network.

`dotnet format --verify-no-changes` currently fails on a pre-existing line-ending/encoding baseline; it is **not** a passing gate.

### Build/publish flow caveats

- `./build.ps1 -Deploy` (default `-Deploy:$true`) forces Release + win-x64, publishes a **self-contained single-file EXE**, and copies it into hardcoded local machine folders (`C:\GD\i`, `C:\GD\tc\SZA\_APP`). Pass `-Deploy:$false` for an ordinary solution build. This is a local install, **not** a release.
- Never run `./scripts/build-local.ps1` unless the user explicitly requests a commit - it stages and commits.
- `./build.ps1` never releases. This repo's autonomy verdict is **ask first** for every publishing action; the rule it applies has one home in the canon (`RELEASE_AND_DISTRIBUTION.md` §1).

## Architecture

Four projects with a strict one-way dependency graph - **Core must never reference WPF, App, tools, or tests**:

```
StreamsPlayer.App (WPF UI) ─┐
StreamsPlayer.CatalogHarness ┤──► StreamsPlayer.Core (platform-neutral)
StreamsPlayer.Core.Tests ────┘
```

- **`src/StreamsPlayer.Core`** - all catalog contracts, parsing, merge, and persistence. Pure .NET, no UI. Key pieces: [Models.cs](src/StreamsPlayer.Core/Models.cs) (records + enums, including CLI arg parsing in `StreamLaunchRequest.Parse`), [StreamBankReader.cs](src/StreamsPlayer.Core/StreamBankReader.cs) → [StreamCatalogCsvParser.cs](src/StreamsPlayer.Core/StreamCatalogCsvParser.cs) (RFC-4180 CSV), [CatalogMerger.cs](src/StreamsPlayer.Core/CatalogMerger.cs), [StreamCatalogStore.cs](src/StreamsPlayer.Core/StreamCatalogStore.cs), and [StreamCatalogService.cs](src/StreamsPlayer.Core/StreamCatalogService.cs) (network refresh orchestration).
- **`src/StreamsPlayer.App`** - WPF app (`AssemblyName` = `StreamsPlayer`). `MainWindow` and `PlayerWindow` are the primary surfaces; everything else (`SettingsWindow`, `AddStreamWindow`, `CollectionsWindow`, `HiddenChannelsWindow`, `ImportPreviewWindow`, `ImportUrlWindow`, `LanguageWindow`, `ListeningHistoryWindow`, `PlaybackFailureDialog`) is a modal dialog - enumerate from `*.xaml` rather than trusting a list here. `MainWindow` is split into `MainWindow.<Concern>.cs` partials (sixteen of them; glob, do not memorize).
- **`tools/StreamsPlayer.CatalogHarness`** - console diagnostic that exercises the live catalog contract. `Console.WriteLine` logging is acceptable here only.

### Key data-flow contracts (do not break without a product decision + updated tests)

- **Catalog source is a published external contract**: a ZIP at `StreamCatalogService.CatalogUrl` (FastMediaSorter release). `streams.csv` **must be the first ZIP entry**; the reader rejects the bank otherwise. Optional `favicon-atlas.png`, capped by `StreamBankReader.MaximumAtlasBytes` (30 MB - the live atlas passed 3.9 MB in 2026-07 and grows with the channel count; read the constant, never a number from prose). An atlas over the cap is silently dropped, not an error.
- **The atlas and the CSV are one pair**: `favicon_index` is an offset into the atlas that shipped in the *same* ZIP. Never combine a CSV from one bank build with an atlas from another - the result is wrong icons, not missing ones.
- **A second, optional network artifact**: the channel-preview atlas + coords pair (`ChannelPreviewAtlasService`, revision-gated `v1`), from the same FastMediaSorter release. It downloads only after the user accepts the offer, which is what keeps the explicit-refresh rule intact.
- **Merge protects user data**: `CatalogMerger.Merge` keys channels by URL. It only updates/removes rows whose `SourceOrigin == Catalog`. `MANUAL` and `IMPORTED` (`SourceOrigin.Manual`/`Imported`) rows are never touched by a refresh.
- **Refresh is explicit only**: there are no automatic background catalog downloads. Do not add any.
- **There is no fallback if the download fails**: `RefreshAsync` throws and the UI reports it; a first launch without network leaves the catalog empty. Changing that is the subject of `SP-0052` (a bundled snapshot applied only on explicit user consent) - **not implemented; do not describe it as shipped**.
- **Local state** lives at `%LOCALAPPDATA%\StreamsPlayer` - `catalog-state.json` (written atomically via temp file + move), `favicon-atlas-<guid>.png` (unreferenced ones are pruned on every save), the `grid-previews/` cache, and two log generations (`Current.log`, `Previous.log`, rotated on launch). Persisted via `CatalogState` (a record; JSON with per-enum *tolerant* converters wired in `StreamCatalogStore`, so one unreadable enum value costs that field and not the whole state).

### Media playback

Audio uses WPF `MediaElement`. Player video/RTSP goes through the backend selected in Settings - LibVLC by default, FlyleafLib as an opt-in (`VideoBackendFactory`, `CatalogState.VideoBackend`); grid preview capture is always LibVLC. The LibVLC path uses a fixed 15s live buffer (4s when a stalled stream is re-opened). The Core library has no media dependency.

## Conventions

- Nullable reference types and implicit usings are on. PascalCase types/members, camelCase locals. Keep files under ~500 lines; WPF windows coordinate UI only.
- No raw logging facade in App/Core beyond the existing `CurrentLog` (`App.xaml.cs` wires it to unhandled-exception handlers). Don't add ad-hoc `Console.WriteLine` to App or Core.
- Localization is thirteen languages via `Localization.<code>.xaml` resource dictionaries, and **the shipped list is declared once** - `InterfaceLanguages` in `StreamsPlayer.Core` (per-surface codes, culture, right-to-left flag). Never restate it; the PowerShell tooling reads it from the built assembly via `tools/InterfaceLanguages.ps1`. `CatalogState.Language` is nullable: absent means "never chosen", and an unreadable value degrades to English without touching the rest of the state. `tests/StreamsPlayer.Core.Tests/LocalizationParityTests.cs` gates key, placeholder and layout-direction parity in CI.
- Versioning: `YY.MMDD.HHmm` (UTC), set in [Directory.Build.props](Directory.Build.props). A release version must exceed every published version; never reuse a timestamp.

## Workflow tooling

This repo runs the Universal Agent Kit method. **`AGENTS.md` is the authoritative rules file** - read it and the applicable `docs/agent/` document before non-trivial work. The method in one line: research before acting, split *what/why* (`/streamsplayer-spec`) from *how* (`/streamsplayer-spec-tech`), plan in verifiable phases, stay cheap when the task is small, and prove "done" with evidence, not a green build. A changed GUI action needs run-and-observe evidence.

**Communication**: one home in the canon - `AUTHOR.md` "Language" and `AI_USAGE.md` §7. The only repo-local addition is that replies carry the local time supplied in the prompt.

- **Skills.** The same procedures are available three ways: native Claude Code slash commands in [.claude/commands/](.claude/commands/) (`/streamsplayer-quick`, `-fix`, `-research`, `-spec`, `-spec-tech`, `-spec-dev`, `-spec-check`, `-spec-fix`, `-spec-all`, `-backlog`, `-park`, `-ui-clarify`, `-verify`, `-review`, `-git`, `-caveman[-commit|-review]`), Codex/`$`-invoked skills in [.agents/skills/](.agents/skills/), and the shared `SKILL.md` bodies both point to. The `.claude/commands/*` files are thin wrappers - the procedure lives in `.agents/skills/streamsplayer-*/SKILL.md`.
- **Agents.** Role subagents in [.claude/agents/](.claude/agents/) (`streamsplayer-rd-lead` is the default orchestrator, plus `-solution-researcher`, `-implementer`, `-doc-writer`), mirrored from [.codex/agents/](.codex/agents/).
- **Method docs.** [docs/agent/](docs/agent/): `SPEC_LIFECYCLE`, `CODE_QUALITY`, `VALIDATION`, `RESEARCH_INDEX`, `AGENT_MEMORY`, `COST`.
- **Tickets.** Spec-driven planning under [PLAN/](PLAN/): `SP-NNNN` ids, states `Draft → Approved → Tactical → In Progress → Implemented → Verified` (+ `Partial`/`Broken`/`Block*`). Status comes from the working tree, never the filename. Verified strategic tickets and their tactical folders move to `PLAN/DONE/`.
- **Memory.** File-based, committed, shared across tools: [memory/MEMORY.md](memory/MEMORY.md) is the always-loaded index (types: `user`, `feedback`, `project`, `reference`); discipline in `docs/agent/AGENT_MEMORY.md`.
- **Canon.** The portfolio-wide SZA Unified Rules ship as the `sza` Claude Code plugin and are the source of truth for universal conventions. Consumption model is **reference** - they are not re-authored here (see `AGENTS.md` -> "SZA Unified Rules (canon)"). Load a skill rather than the whole canon: `sza:release`, `sza:store-publish`, `sza:feature-to-site`, `sza:spec-to-audit`. Repo overlay facts and legitimate divergences are recorded in the canon's `contrib/streams_player.md`; adoption is stamped in `.sza-canon.json`. Universal-rule fixes land in the canon first.
