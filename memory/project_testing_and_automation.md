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
