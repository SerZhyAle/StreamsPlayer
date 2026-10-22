---
name: project_testing_and_automation
description: UI Automation traps, sandbox isolation, smoke tests, and power-state testing methods
type: project
---

# Testing, UI Automation & Verification Methods

- **UI Automation (UIA) rules & traps (SP-0030, SP-0058, SP-0114, SP-0124):**
  - Modals, Settings, History, and MessageDialogs are top-level windows (`EnumWindows`), not descendants of `MainWindow`.
  - Virtualized channel cards (`DataItem`) have no UIA children; trigger via `--id` / `--url` command-line launch requests or geometric coordinates.
  - `TabControl` child elements are only created when the tab is selected (`SelectionItemPattern`).
  - Always call `user32!SetProcessDPIAware()` before coordinate calculation or `CopyFromScreen`.
  - Foreground-lock bypass using ALT tap collapses WPF content tree to chrome; follow with Escape key.
- **Environment & sandbox isolation:**
  - `Enter-SpSandbox` and `Exit-SpSandbox` isolate `%LOCALAPPDATA%\StreamsPlayer`. Ensure processes are terminated before sandbox cleanup.
- **Smoke playback gate (scripts/smoke-playback.ps1):**
  - Mandatory offline/live separation: unit tests run offline in CI; `smoke-playback.ps1` validates shipping binaries against live endpoints before release.
- **Power assertions (WakeGuard):**
  - Ref-counted `SetThreadExecutionState` calls appear as a single entry per thread in `powercfg /requests`. Windows audio stack adds its own system-level entry during active audio playback.
- **Driving the real app against a scripted exchange server (SP-0222, 2026-10-21).**
  - `STREAMSPLAYER_DATA_DIRECTORY` relocates the profile (and makes it an instance of its own, so the owner's running copy is untouched). A TLS server that speaks the exchange wire can be hosted by a throw-away xunit test in `StreamsPlayer.Exchange.Tests` (it compiles the App's exchange sources, so `ExchangeAccountStore` can write the DPAPI account into that profile). A `~104 ms / 4 frames` loop of silent MPEG-1 Layer III frames (`FF FB 90 64` + zeros, 417 bytes) over plain HTTP plays as a real audio broadcast.
  - An **owned window is nested under its owner in the UIA tree**, not a child of the desktop: `FindAll(Children)` from the root misses it. Search the main window's `Descendants` for `ControlType.Window`, or `AutomationElement.FromHandle`.
  - Build, tests and review had all passed while three windows could not open: a `StaticResource` is resolved when a window loads, and `SettingsLinkButton` lived only inside `SettingsWindow.xaml`. `StaticResourceResolutionTests` now reads that rule for every window; a window that no test constructs still needs a run.
- **Traps met while building SP-0226 (2026-10-21).**
  - `StreamsPlayer.Exchange.Tests` compiles the App's exchange sources by link and holds `InternalsVisibleTo` from Core, so an App file that touches a Core `internal` member builds green there and fails in `StreamsPlayer.App` - build the App itself (`dotnet build src/StreamsPlayer.App`) after any Core visibility choice.
  - `scripts/check.ps1` cannot build while the owner's Release copy is running (`StreamsPlayer.Core.dll` in `src/StreamsPlayer.App/bin/Release/...` is locked); do not kill it - run the three test projects in Release and compile the App with `-p:OutDir=temp/<ticket>/..` instead, and say the gate was not run.
  - Patching files from PowerShell: a double-quoted string turns `` `e `` into ESC (0x1B) and drops the backtick of `` `word` `` (use single quotes or here-strings for text with markdown backticks), and in `@('a' + "`n", 'b')` the comma binds before `+` (parenthesize each element).
