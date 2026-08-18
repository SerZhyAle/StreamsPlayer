# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

StreamsPlayer is a Windows desktop application (.NET 10, WPF) for internet radio, live video, and RTSP. It is an independent product owned by Serhii Zhyhunenko (`SerZhyAle`). `AGENTS.md` is the authoritative contributor guide; this file summarizes what is not obvious from the code.

## Commands

Run from the repository root. A human types these in PowerShell; an **agent must route every `.ps1` through the interpreter** - `pwsh -NoProfile -File ./build.ps1 ..` - because a bare `./build.ps1` in a Bash tool call is refused by the canon's `guard-bash` hook, and a backgrounded one would report exit 0 over a failed build.

- `pwsh -NoProfile -File ./build.ps1 -Test -Deploy:$false` - restore, build, run tests (Debug by default; add `-Configuration Release` to match CI). **`-Deploy` defaults to `$true`**, so a bare `-Test` forces Release *and* deploys to the local machine folders; `-Configuration Debug` then throws.
- `pwsh -NoProfile -File ./run.ps1` - restore, build (Debug), launch the app; it always passes `-Deploy:$false`. `build.ps1 -Run` is *not* the same thing: it deploys first and runs Release.
- `pwsh -NoProfile -File ./scripts/check.ps1` - the release-parity gate: Release restore + build + `dotnet test`. Run this before proposing a release.
- `dotnet test StreamsPlayer.sln -c Release --no-build` - run all tests.
- Run one test: `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~CatalogMergerTests"` (all tests live in `StreamsPlayer.Core.Tests`; the App and tools have no tests).
- `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png` - validate the live stream-bank contract against the network.

`dotnet format --verify-no-changes` currently fails on a pre-existing line-ending/encoding baseline; it is **not** a passing gate.

### Build/publish flow caveats

- `build.ps1 -Deploy` (the default) forces Release, publishes a **self-contained single-file EXE**, and copies it into hardcoded local machine folders (`C:\GD\i`, `C:\GD\tc\SZA\_APP`). Pass `-Deploy:$false` for an ordinary solution build. This is a local install, **not** a release. It throws if `-Configuration` is bound to non-Release or `-Runtime` to anything but `win-x64`; it does not silently force either.
- Never run `scripts/build-local.ps1` unless the user explicitly requests a commit - it `git add --all` then commits.
- `build.ps1` never releases. This repo's autonomy verdict is **ask first** for every publishing action; the rule it applies has one home in the canon (`RELEASE_AND_DISTRIBUTION.md` §1).

## Architecture

Four projects with a strict one-way dependency graph - **Core must never reference WPF, App, tools, or tests**:

```
StreamsPlayer.App (WPF UI) ─┐
StreamsPlayer.CatalogHarness ┤──► StreamsPlayer.Core (platform-neutral)
StreamsPlayer.Core.Tests ────┘
```

- **`src/StreamsPlayer.Core`** - all catalog contracts, parsing, merge, and persistence. Pure .NET, no UI. Key pieces: [Models.cs](src/StreamsPlayer.Core/Models.cs) (records + enums, including CLI arg parsing in `StreamLaunchRequest.Parse`), [StreamBankReader.cs](src/StreamsPlayer.Core/StreamBankReader.cs) → [StreamCatalogCsvParser.cs](src/StreamsPlayer.Core/StreamCatalogCsvParser.cs) (RFC-4180 CSV), [CatalogMerger.cs](src/StreamsPlayer.Core/CatalogMerger.cs), [StreamCatalogStore.cs](src/StreamsPlayer.Core/StreamCatalogStore.cs), and [StreamCatalogService.cs](src/StreamsPlayer.Core/StreamCatalogService.cs) (network refresh orchestration).
- **`src/StreamsPlayer.App`** - WPF app (`AssemblyName` = `StreamsPlayer`). `MainWindow` and `PlayerWindow` are the primary surfaces; every other `*Window.xaml` / `*Dialog.xaml` is a modal dialog - **enumerate them by glob; no list is kept here**, because the last one drifted in both directions at once (it named a `LanguageWindow` that does not exist and missed three that do). `MainWindow` is split into `MainWindow.<Concern>.cs` partials - glob those too, and do not memorize a count.
- **`tools/StreamsPlayer.CatalogHarness`** - console diagnostic that exercises the live catalog contract. `Console.WriteLine` logging is acceptable here only.

### Key data-flow contracts (do not break without a product decision + updated tests)

- **Catalog source is a published external contract**: a ZIP at `StreamCatalogService.CatalogUrl` (FastMediaSorter release). `streams.csv` **must be the first ZIP entry**; the reader rejects the bank otherwise. Optional `favicon-atlas.png`, capped by `StreamBankReader.MaximumAtlasBytes` - **read the constant and its comment, never a size from prose**; the atlas grows with the channel count. An atlas over that cap is silently dropped, not an error. The whole archive has a separate, larger cap (`StreamCatalogService.MaximumArchiveBytes`), and exceeding *that* one **is** an error.
- **The atlas and the CSV are one pair**: `favicon_index` is an offset into the atlas that shipped in the *same* ZIP. Never combine a CSV from one bank build with an atlas from another - the result is wrong icons, not missing ones.
- **A second, optional network artifact**: the channel-preview atlas + coords pair (`ChannelPreviewAtlasService`, revision-gated `v1`), from the same FastMediaSorter release. It downloads only after the user accepts the offer, which is what keeps the explicit-refresh rule intact.
- **Merge protects user data**: `CatalogMerger.Merge` keys channels by URL and only ever touches rows whose `SourceOrigin == Catalog`; `MANUAL` and `IMPORTED` (`SourceOrigin.Manual`/`Imported`) rows are never touched by a refresh. Removal is **not** unconditional - it is gated on `CatalogMergeOptions.RemoveMissing`, which the snapshot path deliberately passes as `false`; the same options record also carries `FaviconSource`. Read `CatalogMergeOptions` before assuming a merge mode.
- **Refresh is explicit only**: there are no automatic background catalog downloads. Do not add any.
- **`RefreshAsync` throws when the download fails** - there is no network fallback inside it. A first launch without network is *not* empty, though: `SP-0052` **shipped** a bundled catalog snapshot (`BundledCatalogSnapshot`, `CatalogSnapshotService`, `FirstRunCatalogWindow`), applied only on explicit user consent. `StreamsPlayer.Core.csproj` embeds `Resources\catalog-snapshot.zip` and its `RequireBundledCatalogSnapshot` target **fails the build** when it is absent (override: `-p:AllowMissingCatalogSnapshot=true`), and `scripts/release.ps1` makes regenerating it a release blocker. The ticket is `Implemented`, not yet `Verified` - describe it as shipped and awaiting manual verification.
- **Local state** lives at `%LOCALAPPDATA%\StreamsPlayer` - `catalog-state.json` (written atomically via temp file + move), `favicon-atlas-<guid>.png` (unreferenced ones are pruned on every save), the `grid-previews/` cache, and the last ten launches' logs (`Current.log` plus `Session-<yyyyMMdd-HHmm>.log`, retired and pruned on launch by `DiagnosticLogFiles`; the session stamp is local time). Persisted via `CatalogState` (a record; JSON with per-enum *tolerant* converters wired in `StreamCatalogStore`, so one unreadable enum value costs that field and not the whole state).

### Media playback

Audio uses WPF `MediaElement`. Player video/RTSP goes through the backend selected in Settings - LibVLC by default, FlyleafLib as an opt-in (`VideoBackendFactory`, `CatalogState.VideoBackend`); grid preview capture is always LibVLC. The LibVLC path uses a fixed 15s live buffer (4s when a stalled stream is re-opened). The Core library has no media dependency.

## Conventions

- Nullable reference types and implicit usings are on. PascalCase types/members, camelCase locals. Keep files under ~500 lines; WPF windows coordinate UI only. **Honest state of that aim**: `MainWindow.xaml.cs` and `PlayerWindow.xaml.cs` are well past it (~1300 / ~1100 lines) and are the standing exceptions - extract from them rather than adding, and do not read the budget as already met.
- No raw logging facade in App/Core beyond the existing `CurrentLog` (`App.xaml.cs` wires it to unhandled-exception handlers). Don't add ad-hoc `Console.WriteLine` to App or Core.
- Localization is thirteen languages via `Localization.<code>.xaml` resource dictionaries, and **the shipped list is declared once** - `InterfaceLanguages` in `StreamsPlayer.Core` (per-surface codes, culture, right-to-left flag). Never restate it; the PowerShell tooling reads it from the built assembly via `tools/InterfaceLanguages.ps1`. `CatalogState.Language` is nullable: absent means "never chosen", and an unreadable value degrades to English without touching the rest of the state. `tests/StreamsPlayer.Core.Tests/LocalizationParityTests.cs` gates key, placeholder and layout-direction parity in CI, and `LocalizedCallSiteTests.cs` gates those strings against the code that formats them - it reads the App's own `*.cs` and `*.xaml` as linked test **data** (same terms as the dictionaries: no project reference, never loaded) and fails on a call site whose argument count disagrees with its string's placeholders, on a key that no longer exists, and on a placeholder-bearing key bound where nothing can supply arguments. Adding a placeholder to a shipped string therefore now forces its call site. All rendering goes through `LocalizedFormat.Apply` in Core, which never throws: a shortfall renders blanks rather than killing the `async void` handler (SP-0057).
- Versioning: `YY.MMDD.HHmm` in the author's **local** time (Europe/Malta), set in [Directory.Build.props](Directory.Build.props) - the stamp exists so the owner can see when a build was made without converting time zones. Anything at or below `26.0806.2131` was stamped in UTC and reads 1-2 h early. A release version must exceed every published version; never reuse a timestamp. For a real release the value comes from the `v*` tag via `release.yml`, not from the props file.

## Workflow tooling

This repo runs the Universal Agent Kit method. **`AGENTS.md` is the authoritative rules file** - read it and the applicable `docs/agent/` document before non-trivial work. The repo-local shape of the method: split *what/why* (`/streamsplayer-spec`) from *how* (`/streamsplayer-spec-tech`), and reach for the cheapest sufficient rung on the `/streamsplayer-*` ladder. The evidence rule, the cost discipline and the research-first rule are the canon's, not this file's.

**Repo-local evidence delta**: a changed GUI action needs run-and-observe evidence, not merely a build (`docs/agent/VALIDATION.md`).

**Communication** has one home in the canon - `AUTHOR.md` "Language" and `AI_USAGE.md` §7. No repo-local addition. In particular, do **not** prefix replies with a clock time: `AI_USAGE.md` §7 explicitly supersedes the prompt-timestamp rule this file used to carry.

- **Skills.** The same procedures are available three ways: native Claude Code slash commands in [.claude/commands/](.claude/commands/) (`/streamsplayer-quick`, `-fix`, `-research`, `-spec`, `-spec-tech`, `-spec-dev`, `-spec-check`, `-spec-fix`, `-spec-all`, `-backlog`, `-park`, `-ui-clarify`, `-verify`, `-review`, `-git`, `-caveman[-commit|-review]`), Codex/`$`-invoked skills in [.agents/skills/](.agents/skills/), and the shared `SKILL.md` bodies both point to. The `.claude/commands/*` files are thin wrappers - the procedure lives in `.agents/skills/streamsplayer-*/SKILL.md`.
- **Agents.** Role subagents in [.claude/agents/](.claude/agents/) (`streamsplayer-rd-lead` is the default orchestrator, plus `-solution-researcher`, `-implementer`, `-doc-writer`), mirrored from [.codex/agents/](.codex/agents/).
- **Method docs.** [docs/agent/](docs/agent/): `SPEC_LIFECYCLE`, `CODE_QUALITY`, `VALIDATION`, `RESEARCH_INDEX`, `AGENT_MEMORY`, `COST`.
- **Tickets.** Spec-driven planning under [PLAN/](PLAN/): `SP-NNNN` ids, states `Draft → Approved → Tactical → In Progress → Implemented → Verified` (+ `Partial`/`Broken`/`Block*`). Status comes from the working tree, never the filename. Verified strategic tickets and their tactical folders move to `PLAN/DONE/`.
- **Memory.** File-based, committed, shared across tools: [memory/MEMORY.md](memory/MEMORY.md) is the always-loaded index (types: `user`, `feedback`, `project`, `reference`); discipline in `docs/agent/AGENT_MEMORY.md`.
- **Canon.** The portfolio-wide SZA Unified Rules ship as the `sza` Claude Code plugin and are the source of truth for universal conventions. Consumption model is **reference** - they are not re-authored here (see `AGENTS.md` -> "SZA Unified Rules (canon)"). Load a skill rather than the whole canon: `sza:release`, `sza:store-publish`, `sza:feature-to-site`, `sza:spec-to-audit`, `sza:adopt-canon`, `sza:agent-cost`, `sza:caveman`. Repo overlay facts and legitimate divergences are recorded in the canon's `contrib/streams_player.md`; adoption is stamped in `.sza-canon.json`. Universal-rule fixes land in the canon first.
- **Canon hooks.** The plugin enforces five behaviours at the tool call, so they are not restated as prose here: the Bash-safety family (a disk-wide `find`, a `.ps1` or a PowerShell cmdlet in Bash command-head position, a missing interpreter, a slash-argument MSYS mangling), a refusal to background a gate or a closure facade, and a rewrite of an uncapped large read. This repo registers **no hooks of its own** - do not add a local copy of one the canon ships.
