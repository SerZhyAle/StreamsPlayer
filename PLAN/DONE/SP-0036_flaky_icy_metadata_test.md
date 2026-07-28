# SP-0036: Flaky ICY metadata test

**Status:** Verified

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
failing run and the passing re-run as evidence. It failed once more in an unrelated full run on
2026-07-28, which is what brought the ticket forward.

## Cause (2026-07-28)

The test observed the reader through `Progress<string?>`. `Progress<T>` is defined to *post* each
callback: with no ambient `SynchronizationContext` - which is the case in this test - every `Report`
becomes its own thread-pool work item, and two work items are free to run in either order. The reader
is not at fault: `IcyMetadataReader.PumpAsync` reports strictly in sequence inside one loop
(`IcyMetadataReader.cs:124-128`), one metadata block at a time. The ordering was lost only in the
observer, which is exactly what the failure showed - the first assertion saw the second title.

The application is unaffected: `MainWindow.NowPlaying.cs:45` builds its `Progress<string?>` on the UI
thread, so its callbacks run in dispatcher order.

## Resolution

The test now observes through a `TitleRecorder` that implements `IProgress<string?>` directly, so
`Report` runs inline on the reader's thread and the recorded list *is* the reader's order by
construction. The assertion is on the whole ordered sequence
(`Assert.Equal(["Test Artist - Test Song", "Second Track"], recorder.Titles)`), not on a snapshot. The
`Task.WhenAny(.., Task.Delay(12s))` wait became `WaitAsync(12s)` on the recorder's completion signal -
a hang guard, not a timing crutch. The second test in the class shared the same defect in weaker form
(an unsynchronized `bool` written from a pool thread) and uses the recorder too.

No retry attribute, no `Thread.Sleep`, and no `Task.Delay` were added.

## Verification (2026-07-28)

- 200 consecutive runs of the class (`dotnet test -c Release --no-build --filter
  FullyQualifiedName~IcyMetadataReaderTests` in a loop, `temp/scratch/icy-200.ps1`) -
  expected: 200/200 | actual: `FINAL: pass=200 fail=0`.
- `./scripts/check.ps1` (Release restore + build + test) - expected: green |
  actual: `Total tests: 381, Passed: 381`, 0 warnings, 0 errors.
