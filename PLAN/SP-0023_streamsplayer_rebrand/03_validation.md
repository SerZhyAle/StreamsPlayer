# Phase 03 — Validation

**Status:** Approved

1. Run `./scripts/check.ps1`. Check: Release build succeeds and all tests pass under renamed solution/project identities.
2. Scan all tracked product files, including `PLAN/DONE`. Check: no stale technical or public former-identity references remain.
3. Launch the application twice, once in English and once in Russian. Check: window/title/about labels show `STREAMS Player` and `Трансляции` respectively; the created local state/log path is `%LOCALAPPDATA%\StreamsPlayer`.
