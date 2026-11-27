---
paths:
  - "src/**"
  - "tests/**"
  - "help/**"
  - "tools/help/**"
---

# Code, UI, media and localization

Detail moved verbatim from `CLAUDE.md` and `AGENTS.md` on 2026-11-25; the always-loaded core is `AGENTS.md`.

## Architecture

Five projects with a strict one-way dependency graph - **Core must never reference WPF, App, Cli, tools, or tests**; the console companion `src/StreamsPlayer.Cli` (SP-0275) references Core only and is a `Main` over `AppCommandConsole`:

```
StreamsPlayer.App (WPF UI) ─┐
StreamsPlayer.Cli (console) ─┤
StreamsPlayer.CatalogHarness ┤──► StreamsPlayer.Core (platform-neutral)
StreamsPlayer.Core.Tests ────┘
```

- **`src/StreamsPlayer.Core`** - all catalog contracts, parsing, merge, and persistence. Pure .NET, no UI. Key pieces: [Models.cs](../../src/StreamsPlayer.Core/Models.cs) (records + enums, including CLI arg parsing in `StreamLaunchRequest.Parse`), [StreamBankReader.cs](../../src/StreamsPlayer.Core/StreamBankReader.cs) → [StreamCatalogCsvParser.cs](../../src/StreamsPlayer.Core/StreamCatalogCsvParser.cs) (RFC-4180 CSV), [CatalogMerger.cs](../../src/StreamsPlayer.Core/CatalogMerger.cs), [StreamCatalogStore.cs](../../src/StreamsPlayer.Core/StreamCatalogStore.cs), and [StreamCatalogService.cs](../../src/StreamsPlayer.Core/StreamCatalogService.cs) (network refresh orchestration).
- **`src/StreamsPlayer.App`** - WPF app (`AssemblyName` = `StreamsPlayer`). `MainWindow` and `PlayerWindow` are the primary surfaces; every other `*Window.xaml` / `*Dialog.xaml` is a modal dialog - **enumerate them by glob; no list is kept here**, because the last one drifted in both directions at once (it named a `LanguageWindow` that does not exist and missed three that do). `MainWindow` is split into `MainWindow.<Concern>.cs` partials - glob those too, and do not memorize a count.
- **`tools/StreamsPlayer.CatalogHarness`** - console diagnostic that exercises the live catalog contract. `Console.WriteLine` logging is acceptable here only.

### Media playback

Radio uses an audio-only LibVLC engine (`StandardAudioPlayback`, SP-0104; it replaced WPF `MediaElement`), and a FastMediaSorter broadcast takes its own low-latency path (`FastMediaSorterAudioPlayback`). Player video/RTSP goes through the backend selected in Settings - LibVLC by default, FlyleafLib as an opt-in (`VideoBackendFactory`, `CatalogState.VideoBackend`); grid preview capture is always LibVLC. The LibVLC path uses a fixed 15s live buffer (4s when a stalled stream is re-opened). The Core library has no media dependency.

## Conventions

- Nullable reference types and implicit usings are on. PascalCase types/members, camelCase locals. Keep files under ~500 lines; WPF windows coordinate UI only. **Honest state of that aim**: `MainWindow.xaml.cs` and `PlayerWindow.xaml.cs` are well past it (~1300 / ~1100 lines) and are the standing exceptions - extract from them rather than adding, and do not read the budget as already met.
- No raw logging facade in App/Core beyond the existing `CurrentLog` (`App.xaml.cs` wires it to unhandled-exception handlers). Don't add ad-hoc `Console.WriteLine` to App or Core.
- Localization is thirteen languages via `Localization.<code>.xaml` resource dictionaries, and **the shipped list is declared once** - `InterfaceLanguages` in `StreamsPlayer.Core` (per-surface codes, culture, right-to-left flag). Never restate it; the PowerShell tooling reads it from the built assembly via `tools/InterfaceLanguages.ps1`. `CatalogState.Language` is nullable: absent means "never chosen", and an unreadable value degrades to English without touching the rest of the state. `tests/StreamsPlayer.Core.Tests/LocalizationParityTests.cs` gates key, placeholder and layout-direction parity in the release suite, and `LocalizedCallSiteTests.cs` gates those strings against the code that formats them - it reads the App's own `*.cs` and `*.xaml` as linked test **data** (same terms as the dictionaries: no project reference, never loaded) and fails on a call site whose argument count disagrees with its string's placeholders, on a key that no longer exists, and on a placeholder-bearing key bound where nothing can supply arguments. Adding a placeholder to a shipped string therefore now forces its call site. All rendering goes through `LocalizedFormat.Apply` in Core, which never throws: a shortfall renders blanks rather than killing the `async void` handler (SP-0057).

## Project layout

- `src/StreamsPlayer.Core` - platform-neutral catalog contracts, parsing, merge
  and persistence.
- `src/StreamsPlayer.App` - WPF Windows desktop application.
- `src/StreamsPlayer.Cli` - the console companion of the command line (SP-0275, `APP-COMMANDS` rule 9): `StreamsPlayerCli.exe`, a console-subsystem executable that references `StreamsPlayer.Core` only (never WPF or App) and is only a `Main` over `AppCommandConsole` in Core. The App project builds it and publishes it into its own publish folder, so the zip, the installer and the MSIX carry it; `scripts/Assert-ConsoleCompanion.ps1` judges that folder. The manifest's per-command `exe` says which executable serves which verb (the ones that need no window of their own go to the companion).
- `tests/StreamsPlayer.Core.Tests` - unit and contract tests.
- `tools/StreamsPlayer.CatalogHarness` - live-bank diagnostic harness.
- `tools/InterfaceLanguages.ps1` - the single shipped-language list, read from the built assembly and dot-sourced by the site and Store tooling.
- `help/` - the in-app help sources (SP-0230, `CONTEXT-HELP`): **`help/help-map.json` is the help map** (one row per `Window` class and settings page; embedded in `StreamsPlayer.Core`), `help/help-baseline.txt` (accepted debt, may only fall), `help/content/<locale>/` (copy), `help/templates/`. `tools/help/build-help.ps1` renders it into `docs/help/`, which ships beside the executable as `help\`. A new `Window` class owes a map row, and a changed label or control owes its copy, in the same edit; `HelpGateTests` and `scripts/check-help.ps1` fail otherwise.
