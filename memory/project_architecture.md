---
name: project_architecture
description: Application lifecycle, shutdown ordering, persistence, concurrency, and single instance architecture
type: project
---

# Project Architecture & Lifecycle Constraints

- **Single-instance lifecycle (SP-0118, APP-ACTIVATION):**
  - Builds from 2026-09-25 are single-instance per session. A second launch forwards over the named pipe and exits 0 in ~0.2s.
  - The running copy logs `LAUNCH FORWARDED` and plays the target. `--url` invocations land in the *running* instance's `Current.log`.
  - If the running copy holds the lock but does not answer, the second instance shows a dialog and exits 1.
- **Teardown & shutdown event ordering (SP-0065, SP-0120):**
  - UI events (e.g. `MouseLeave` synthesized by `HwndMouseInputProvider` disposal) continue arriving after `MainWindow_Closed`.
  - Disposing a field is not enough: null it and gate handlers with `_shuttingDown`.
  - `_shuttingDown` latch must be set at the very beginning of `MainWindow_Closing` (before closing child player windows).
  - `ShutdownMode` is `OnExplicitShutdown`. Closing the catalog window is the sole standard path to exit (`MainWindow.CloseWork`, `App.EndAfterCloseWorkAsync`).
- **Resilient persistence & I/O absorption:**
  - `MainWindow.PersistAsync` absorbs and logs I/O failures rather than allowing unhandled `async void` exceptions to crash the process.
  - Debounce any control (such as volume slider) that can fire rapid state saves.
  - `CatalogState` reference equality is a reliable cache key for channel membership, but does not gate mutations of a single channel's fields.
- **Concurrent agent sessions in working tree:**
  - Multiple sessions may operate concurrently. Always re-scan `PLAN/` immediately before allocating ticket IDs.
  - Stage changes by explicit path and inspect `git status` before committing.
  - Prefer targeted edits over whole-file rewrites to avoid stomping concurrent changes.
- **Data directory resolution:**
  - `AppPaths` resolves `%LOCALAPPDATA%\StreamsPlayer` via Windows `SHGetFolderPath`, ignoring environment variable overrides.
