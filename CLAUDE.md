# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

StreamPlayer is a Windows desktop application (.NET 10, WPF) for internet radio, live video, and RTSP. It is an independent product owned by Serhii Zhyhunenko (`SerZhyAle`). `AGENTS.md` is the authoritative contributor guide; this file summarizes what is not obvious from the code.

## Commands

Run from the repository root in PowerShell.

- `./build.ps1 -Test` — restore, build, run tests (Debug by default; add `-Configuration Release` to match CI).
- `./build.ps1 -Run` or `./run.ps1` — restore, build, launch the app.
- `./scripts/check.ps1` — the release-parity gate: Release restore + build + `dotnet test`. Run this before proposing a release.
- `dotnet test StreamPlayer.sln -c Release --no-build` — run all tests.
- Run one test: `dotnet test tests/StreamPlayer.Core.Tests -c Release --filter "FullyQualifiedName~CatalogMergerTests"` (all tests live in `StreamPlayer.Core.Tests`; the App and tools have no tests).
- `dotnet run --project tools/StreamPlayer.CatalogHarness -- artifacts/favicon-sample.png` — validate the live stream-bank contract against the network.

`dotnet format --verify-no-changes` currently fails on a pre-existing line-ending/encoding baseline; it is **not** a passing gate.

### Build/publish flow caveats

- `./build.ps1 -Deploy` (default `-Deploy:$true`) forces Release + win-x64, publishes a **self-contained single-file EXE**, and copies it into hardcoded local machine folders (`C:\GD\i`, `C:\GD\tc\SZA\_APP`). Pass `-Deploy:$false` for an ordinary solution build. This is a local install, **not** a release.
- Never run `./scripts/build-local.ps1` unless the user explicitly requests a commit — it stages and commits.
- A local build is never a release. Do not push tags, cut GitHub releases, submit winget/MSIX manifests, or publish Pages unless the user explicitly asks.

## Architecture

Three projects with a strict one-way dependency graph — **Core must never reference WPF, App, tools, or tests**:

```
StreamPlayer.App (WPF UI) ─┐
StreamPlayer.CatalogHarness ┤──► StreamPlayer.Core (platform-neutral)
StreamPlayer.Core.Tests ────┘
```

- **`src/StreamPlayer.Core`** — all catalog contracts, parsing, merge, and persistence. Pure .NET, no UI. Key pieces: [Models.cs](src/StreamPlayer.Core/Models.cs) (records + enums, including CLI arg parsing in `StreamLaunchRequest.Parse`), [StreamBankReader.cs](src/StreamPlayer.Core/StreamBankReader.cs) → [StreamCatalogCsvParser.cs](src/StreamPlayer.Core/StreamCatalogCsvParser.cs) (RFC-4180 CSV), [CatalogMerger.cs](src/StreamPlayer.Core/CatalogMerger.cs), [StreamCatalogStore.cs](src/StreamPlayer.Core/StreamCatalogStore.cs), and [StreamCatalogService.cs](src/StreamPlayer.Core/StreamCatalogService.cs) (network refresh orchestration).
- **`src/StreamPlayer.App`** — WPF app (`AssemblyName` = `StreamPlayer`). Windows are `MainWindow`, `PlayerWindow`, `SettingsWindow`, `AddStreamWindow`. `MainWindow` is split into partial-class files by concern (`MainWindow.Launch.cs`, `.Settings.cs`, `.Previews.cs`, `.BrowsingSession.cs`, `.Localization.cs`). Grid preview capture and player video use LibVLC.
- **`tools/StreamPlayer.CatalogHarness`** — console diagnostic that exercises the live catalog contract. `Console.WriteLine` logging is acceptable here only.

### Key data-flow contracts (do not break without a product decision + updated tests)

- **Catalog source is a published external contract**: a ZIP at `StreamCatalogService.CatalogUrl` (FastMediaSorter release). `streams.csv` **must be the first ZIP entry**; the reader rejects the bank otherwise. Optional `favicon-atlas.png` (≤4 MB).
- **Merge protects user data**: `CatalogMerger.Merge` keys channels by URL. It only updates/removes rows whose `SourceOrigin == Catalog`. `MANUAL` and `IMPORTED` (`SourceOrigin.Manual`/`Imported`) rows are never touched by a refresh.
- **Refresh is explicit only**: there are no automatic background catalog downloads. Do not add any.
- **Local state** lives at `%LOCALAPPDATA%\StreamPlayer` — `catalog-state.json` (written atomically via temp file + move), favicon atlas PNGs, and the session `Current.log`. Persisted via `CatalogState` (a record; JSON with `JsonStringEnumConverter`).

### Media playback

Audio uses WPF `MediaElement`. Video/RTSP and grid preview capture use bundled LibVLC (`LibVLCSharp`, `VideoLAN.LibVLC.Windows`) with a ~10s live buffer. The Core library has no media dependency.

## Conventions

- Nullable reference types and implicit usings are on. PascalCase types/members, camelCase locals. Keep files under ~500 lines; WPF windows coordinate UI only.
- No raw logging facade in App/Core beyond the existing `CurrentLog` (`App.xaml.cs` wires it to unhandled-exception handlers). Don't add ad-hoc `Console.WriteLine` to App or Core.
- Localization is English + Russian via `Localization.en.xaml` / `Localization.ru.xaml` resource dictionaries; the choice persists in `CatalogState.Language`.
- Versioning: `YY.MMDD.HHmm` (UTC), set in [Directory.Build.props](Directory.Build.props). A release version must exceed every published version; never reuse a timestamp.

## Workflow tooling

This repo ships a "Universal Agent Kit" of `$streamplayer-*` skills in [.agents/skills/](.agents/skills/) and spec-driven planning under [PLAN/](PLAN/) (tickets `SP-000N`, states `Draft → … → Verified`). See `AGENTS.md` for skill routing and the ticket lifecycle. A changed GUI action needs run-and-observe evidence, not just a green build.
