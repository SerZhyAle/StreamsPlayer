# Phase 01 - Core: log file names and previous-session rotation

**Status:** Planned

## Goal

Give the previous session's log a name and a rotation rule, in Core, so both the log facade
(App) and the archive builder agree on it and the rule itself is unit-tested.

## Changes

- New `src/StreamsPlayer.Core/DiagnosticLogFiles.cs`:
  - `public const string CurrentLogName = "Current.log";`
  - `public const string PreviousLogName = "Previous.log";`
  - `public static void RotateCurrentToPrevious(string directory)` - if the current log exists,
    move it over the previous one (`File.Move(..., overwrite: true)`); swallow `IOException`,
    `UnauthorizedAccessException` and `NotSupportedException` only, because a locked or
    ACL-denied file must degrade to "previous log lost", never to a failed launch (spec
    constraint "retention change must remain best-effort").
  - `public static IReadOnlyList<string> ExistingLogs(string directory)` - the current and
    previous log paths that actually exist, current first, for the archive builder.
- New `tests/StreamsPlayer.Core.Tests/DiagnosticLogFilesTests.cs`.

## Verification

`dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~DiagnosticLogFilesTests"`
passes, covering:

1. Rotation with no current log leaves the directory unchanged and does not throw.
2. Rotation moves the current log's content to the previous name.
3. A second rotation overwrites the older previous log (no third generation accumulates).
4. `ExistingLogs` returns current-then-previous and omits absent files.
5. Rotation on a missing directory does not throw.

## Checks

- Status: Implemented.
- expected: 5 facts pass | actual: `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~DiagnosticLogFilesTests"` - Passed 5, Failed 0.
