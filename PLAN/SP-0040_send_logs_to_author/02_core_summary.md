# Phase 02 - Core: the environment summary

**Status:** Planned

## Goal

Produce the plain-text environment file that travels with the logs - the facts the author
currently has to ask for - with no channel data in it (spec criteria 3 and 13).

## Changes

- New `src/StreamsPlayer.Core/DiagnosticEnvironmentSummary.cs`:
  - `public sealed record DiagnosticEnvironment(string AppVersion, string OperatingSystem, string Architecture, string? InterfaceLanguage, MediaBackend MediaBackend, int TotalChannels, int ManualChannels, int ImportedChannels, int PinnedChannels, int HiddenChannels, int CollectionCount, DateTimeOffset? CatalogRefreshedUtc, DateTimeOffset GeneratedUtc)`.
  - `public static string Render(DiagnosticEnvironment environment)` - `KEY=value` lines, one per
    fact, UTC ISO-8601 timestamps, plus one line naming whether the selected backend reports
    stream statistics (`backend_stats=detailed` for LibVLC, `backend_stats=session_only` for
    Flyleaf), which is criterion 13's whole point.
  - `public static DiagnosticEnvironment From(CatalogState state, string appVersion, string operatingSystem, string architecture, DateTimeOffset generatedUtc)` - derives every count from
    the state so the App cannot get them subtly wrong, and never copies a title or a URL.
- New `tests/StreamsPlayer.Core.Tests/DiagnosticEnvironmentSummaryTests.cs`.

## Verification

`dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~DiagnosticEnvironmentSummaryTests"`
passes, covering:

1. `From` counts MANUAL, IMPORTED, catalog, pinned and hidden rows correctly on a mixed state.
2. `Render` output contains no `http`, no `rtsp`, and none of the channel titles present in the
   state it was built from - asserted against a state seeded with a recognisable title and URL.
3. `Render` names the backend and the matching `backend_stats` value for both backends.
4. An absent interface language renders as an explicit "not chosen" token, not an empty value.
5. Every rendered line matches `KEY=value` (greppable, single-line, like the log itself).

## Checks

- Status: Implemented.
- expected: 6 facts pass | actual: `--filter "FullyQualifiedName~DiagnosticEnvironmentSummaryTests"` - Passed 6, Failed 0.
- Deviation: `Render` also emits `state_schema`, and the counts split catalog rows out separately, so the author can see at a glance whether a report comes from a mostly-catalog or mostly-user list.
