# SP-0036: Flaky ICY metadata test

**Status:** Draft

## Problem

`IcyMetadataReaderTests.ReadAsync_ReportsChangedStreamTitlesFromIcyStream` fails intermittently:

```
Expected: "Test Artist - Test Song"
Actual:   "Second Track"
           ↑ (pos 0)
tests/StreamsPlayer.Core.Tests/IcyMetadataReaderTests.cs:55
```

It reported the *second* title where the first was expected, so the assertion on the first observed
title raced the reader. Observed once in a full Release run on 2026-07-27, then passed on an immediate
re-run of the same binary, and passed again in `./scripts/check.ps1` - a genuine flake, not a
regression: nothing under SP-0034 touched `IcyMetadataReader` or its test (`git log` on both paths
predates the ticket).

A test that fails one run in several is worse than a missing test. It trains everyone to re-run
instead of read, and it will eventually mask a real ICY regression.

## Approach

Find the race rather than add a retry or a sleep. Likely candidates, in order: the fake stream hands
the reader both metadata blocks before the first callback is observed; the assertion reads a field the
reader writes from another task without synchronisation; or the test awaits a signal that only
approximates "the first title has been reported".

Make the sequence deterministic - the test should observe an ordered record of every reported title
and assert on that record, not on a snapshot taken at a moment it cannot control.

## Done criteria

- The cause is named in the ticket, not just fixed.
- The test asserts the full ordered sequence of reported titles.
- 200 consecutive runs of the ICY test class pass (`dotnet test --filter FullyQualifiedName~IcyMetadataReaderTests`
  in a loop), recorded as `expected: 200/200 | actual: ...`.
- No `Task.Delay`, no retry attribute, and no `Thread.Sleep` is introduced to make it pass.

## Notes

Found during SP-0034's phase 13 validation and parked there rather than fixed, because it is unrelated
to that ticket's subject and a timing race deserves its own investigation. SP-0034 recorded both the
failing run and the passing re-run as evidence.
