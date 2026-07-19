# Phase 04: Validation and audit

**Produces:** build, test, and GUI evidence plus final ticket audit.

**Status:** Completed

1. Run focused tests and the full Release build/test ladder.
   - Static check: all commands exit zero with zero warnings.
2. Launch the WPF app, exercise List/Grid persistence and responsive layout, scroll to newly visible HTTP(S) video, and observe a real preview frame without UI freeze.
   - Static check: screenshot/evidence under `tmp/` shows a non-favicon 16:9 frame and the app remains responsive.
3. Audit the live working tree against every strategic criterion and update statuses from evidence.
   - Static check: no failed, warning, or open manual criteria remain before `Verified`.
