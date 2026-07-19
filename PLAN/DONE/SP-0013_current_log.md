# SP-0013: Local current-session diagnostic log

**Status:** Verified

## Goal

Create a local `Current.log` for each StreamPlayer session so development and support can inspect important application activity and failures after the fact.

## Why

The application is still maturing. A durable local diagnostic record makes startup, catalog refresh, and unexpected failures actionable without requiring a debugger or any remote telemetry.

## Non-goals

- Add analytics, telemetry, remote upload, or a user-facing log viewer.
- Persist a multi-session log history or alter catalog/playback behaviour.
- Record stream URLs, catalog contents, or other unnecessary user data.

## Constraints

- The current-session file is `%LOCALAPPDATA%\StreamPlayer\Current.log`, alongside the existing local application state; an installed application directory is not assumed writable.
- A new launch replaces the previous `Current.log`; writes must be best-effort and must never prevent the application from starting or closing.
- Log lines use UTC timestamps, a severity, and a concise event or exception message.
- The app exposes a deliberate logging facade; Core remains platform-neutral and no raw logging is introduced there.

## Acceptance criteria

1. Each normal app launch creates a fresh `%LOCALAPPDATA%\StreamPlayer\Current.log` and records startup and shutdown.
2. The log records catalog load and explicit refresh outcomes, including caught failures, without URLs or catalog data.
3. Unhandled WPF, AppDomain, and unobserved-task exceptions are recorded when file logging is available.
4. A logging I/O failure does not change the user-visible application flow.
5. Release build and tests pass; a launched WPF process produces and closes a log file with startup/shutdown entries.

## Risks

Windows can deny or lock the local-data file. The logger therefore degrades silently rather than replacing an application error with a logging error.

## Research

See [research dossier](SP-0013_current_log/research.md).

## Last Audit

- PASS — `CurrentLog` recreates `%LOCALAPPDATA%\StreamPlayer\Current.log`, emits UTC severity-tagged lines, redacts HTTP(S)/RTSP URLs, serializes writes, and makes logging I/O best-effort.
- PASS — `App` records startup/shutdown and WPF dispatcher, AppDomain, and unobserved-task exception surfaces without changing their handling semantics.
- PASS — `MainWindow` records catalog state load and explicit refresh outcomes by counts only; the README and privacy-site localization disclose the local diagnostic file in English, Russian, and Ukrainian.
- PASS — expected: Release build succeeds and Core tests pass | actual: `dotnet build StreamPlayer.sln -c Release --no-restore` succeeded with 0 warnings/errors; `dotnet test StreamPlayer.sln -c Release --no-build` passed 38/38 tests.
- PASS — expected: launched WPF process creates and closes the current-session log | actual: launched Release `StreamPlayer.exe`, then observed `C:\Users\serzh\AppData\Local\StreamPlayer\Current.log` with `Application startup.`, `Catalog state loaded: 2691 channel(s).`, and `Application shutdown.`.
