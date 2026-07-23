# Phase 05 — Localized run-and-observe

**Produces:** PASS/FAIL evidence that details render and the filter behaves.
**Consumes:** Phases 03, 04.

## Automated evidence (done)

1. Release parity gate: `./scripts/check.ps1`
   expected: Release restore + build + all tests pass | actual: Build succeeded; 126/126 tests passed.
2. Smoke launch (`dotnet run --project src/StreamsPlayer.App`): window stayed up ~18s,
   no early crash; log shows `Catalog state loaded: 2182 channel(s)` with no exception —
   proves the new ComboBox style trigger and 4th card row parse at runtime.
3. Live-bank contract check (downloaded `stream-catalog.zip`, entry 0 = `streams.csv`):
   header carries `protocol, format, bitrate, is_live`; bitrate populated for 348/400
   sampled rows as bare integer kbps (64/96/128/192/256), 52 blank. Confirms the parser
   columns exist, `StreamBitrate.TryParseKbps` interprets the real values, the blanks are
   the "visible by default / excluded under minimum" case (AC3), and the 64/128/192/256/320
   filter thresholds match the real distribution.

## Manual GUI observations (pending user test — BlockNeedUserTest)

These require a human glance after a catalog refresh (existing state predates the new
fields; details/filter populate only after **Update catalog**). Launch (`./run.ps1`),
refresh, then observe:
   - A catalog card that has technical claims shows the compact details line
     (format/bitrate/protocol/live); a card without claims shows no details line
     and no error (AC1, present-only fallback).
   - Set **Min bitrate** to 128: rows with a known bitrate ≥ 128 remain; rows
     with a lower, missing, or malformed bitrate disappear; the control shows its
     active indicator; the status count drops accordingly (AC2, AC3).
   - Clear back to **All**: the previously hidden rows return (AC3 default).
   - Combine Min bitrate with a search term and a facet: the result is the
     intersection (AC2).
   - Switch language (RU/EN): the new label + options relabel correctly.
   - Confirm no status bullet changed from playing/observing metadata alone
     (AC5).
3. Record each observation as `expected: ... | actual: ...` in the ticket's
   verification section.

## Static check

Manual run-and-observe — stays `BlockNeedUserTest` in the audit until the GUI
observations above are actually performed and recorded.
