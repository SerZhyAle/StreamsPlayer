# Phase 03 - Core: the archive builder

**Status:** Planned

## Goal

Assemble one mailable ZIP from the existing logs plus the summary text, bounded in size and
self-cleaning, with nothing else in it (spec criteria 2, 9).

## Changes

- New `src/StreamsPlayer.Core/DiagnosticArchiveBuilder.cs`:
  - `public const string ArchiveFolderName = "reports";`
  - `public static string Build(string stateDirectory, string summaryText, DateTimeOffset utcNow)`
    - creates `<stateDirectory>\reports\`, deletes every `StreamsPlayer-logs-*.zip` already there
      (criterion 9: bounded, no accumulation), then writes
      `StreamsPlayer-logs-<yyyyMMdd-HHmmss>.zip` containing `environment.txt` plus each existing
      log from `DiagnosticLogFiles.ExistingLogs`, and returns the full path.
    - copies each log to a temporary file before adding it, because the current log is held open
      by the running session's writer with `FileShare.Read` - `ZipFile.CreateFromDirectory` would
      be the natural call and is the wrong one here.
    - truncates any single log over `MaxLogBytes` (2 MB) to its **last** `MaxLogBytes`, since the
      end of a log is where the failure is, and records the truncation as a line in
      `environment.txt`'s companion note inside the archive rather than silently.
  - Failures propagate: the caller (App) owns the user-visible message, and a silent empty
    archive is worse than an error (spec Decision 5).
- New `tests/StreamsPlayer.Core.Tests/DiagnosticArchiveBuilderTests.cs`.

## Verification

`dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~DiagnosticArchiveBuilderTests"`
passes, covering:

1. With both logs present the archive holds exactly three entries: both log names and
   `environment.txt`.
2. With no previous log the archive holds two entries and still builds.
3. `catalog-state.json` and a favicon atlas sitting in the same directory are **not** in the
   archive - the negative fact criterion 2 turns on.
4. A log opened with `FileShare.Read` by another writer is still archived (the copy path).
5. A second build in the same folder leaves exactly one archive.
6. An oversized log is truncated to the size ceiling, keeps its final line, and the archive
   records that it was truncated.

## Checks

- Status: Implemented.
- expected: 6 facts pass | actual: `--filter "FullyQualifiedName~DiagnosticArchiveBuilderTests"` - Passed 6, Failed 0 (one expectation in the test itself was wrong first time round: ordinal ordering puts `Current.log` and `Previous.log` before `environment.txt`).
- Deviation: no temporary staging file. The log is streamed straight into the zip entry from a `FileShare.ReadWrite | FileShare.Delete` handle, which is what tolerates the live writer; a copy step would have added a failure mode for no benefit.
- The truncation note is appended to `environment.txt` rather than written as a separate entry, so one file answers "what am I looking at".
