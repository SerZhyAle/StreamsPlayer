# Phase 04 - App: the log facade rotates instead of replacing

**Status:** Planned

## Goal

Keep the previous session's log so the archive can carry the session that actually failed
(spec Decision 2, criteria 7 and 8).

## Changes

- `src/StreamsPlayer.App/CurrentLog.cs` - inside the existing constructor `try`, call
  `DiagnosticLogFiles.RotateCurrentToPrevious(directory)` after `Directory.CreateDirectory` and
  before opening the new stream, and use `DiagnosticLogFiles.CurrentLogName` instead of the
  literal. The existing single `catch (Exception)` already guarantees the launch survives; the
  rotation helper additionally swallows its own expected I/O failures so a locked previous log
  does not cost the session its *current* log.
- No signature change, no new dependency on the App side, no change to what is written.

## Verification

- `dotnet build StreamsPlayer.sln -c Release` succeeds.
- `grep -n "Current.log" src/StreamsPlayer.App` returns no literal outside the Core constant's
  own file - the name is declared once.
- Run-and-observe is deferred to phase 09 (criteria 7 and 8 need two real launches).

## Checks

- Status: Implemented.
- expected: Release build clean | actual: `dotnet build StreamsPlayer.sln -c Release` - 0 warnings, 0 errors.
- expected: no `Current.log` literal outside the Core constant | actual: `grep -rn "Current\.log" src/ --include=*.cs` - one hit, `DiagnosticLogFiles.cs:14`.
- Observed (phase 09 criterion 7): launch, close, launch again - `Previous.log` held the first session (4399 bytes, its original timestamp), `Current.log` the second.
