# Research: local current-session diagnostic log

## Current flow

- `App.OnStartup` creates `MainWindow` and shows it; it currently has no error lifecycle hooks (`src/StreamsPlayer.App/App.xaml.cs:13`).
- `MainWindow` already owns `%LOCALAPPDATA%\StreamsPlayer` and initializes its `StreamCatalogStore` there (`src/StreamsPlayer.App/MainWindow.xaml.cs:18-43`).
- Catalog load and explicit refresh both catch exceptions at the UI boundary (`MainWindow.xaml.cs:101-130`, `161-179`), so those failure details can be recorded without changing the UI contract.
- The README documents this local-data directory and the privacy promise of no telemetry (`README.md:55,133-136`).

## Reusable patterns and constraints

- App/Core must use a deliberate logging facade before diagnostic logging; Core must not depend on WPF (`docs/agent/CODE_QUALITY.md`).
- WPF-visible behaviour needs a run-and-observe check, not only a build (`docs/agent/VALIDATION.md`).
- The application directory cannot safely be treated as writable after installation, while the established local-data directory is intended for app-owned files.

## Decision

The App owns a small injected `CurrentLog` facade. `App` creates it before the main window, subscribes to process/WPF exception surfaces, and disposes it on exit. `MainWindow` receives it and logs its catalog lifecycle. The logger omits URLs and catalog contents.

## Open questions

None. `Current.log` is interpreted as one fresh log for the current run, not an accumulating history.
