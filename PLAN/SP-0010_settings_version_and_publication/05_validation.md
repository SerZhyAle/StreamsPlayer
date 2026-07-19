# Phase 05: Validation and audit

**Status:** BlockExternal

**Block:** Re-run MSIX packaging and public-link checks after installing the Windows SDK and publishing the reserved repository/Pages endpoints.

1. Run resource parity, version-format, manifest-consistency, and hardcoded-UI scans.
   - Check: every static predicate returns the expected value with no unexplained match.
2. Run `scripts/check.ps1` and focused persistence tests.
   - Check: build has zero warnings/errors and all tests pass.
3. Exercise Settings in English and Russian, apply every tile size, toggle previews, open links through automation-safe inspection, and verify restart persistence.
   - Check: screenshots and property evidence under `tmp/` prove the visible behavior.
4. Audit every strategic criterion and update ticket status from live evidence.
   - Check: no failed, warning, or open manual criterion remains before `Verified`.

## Evidence

- expected: Release build/tests pass | actual: zero warnings/errors and 37/37 tests passed.
- expected: Settings behavior is observable | actual: English/Russian UI, version/author/links, Large/Small/Medium, preview off/on, restart persistence, and preference cleanup passed; screenshot is `tmp/streamplayer-settings-russian.png`.
- expected: current winget templates validate | actual: filled four-file schema 1.12.0 copy passed `winget validate` without warnings.
- expected: local MSIX build reaches packaging | actual: `makeappx.exe` unavailable; script stopped before publishing or deleting stage output.
- expected: public destinations respond | actual: GitHub author responds, intended repository/Pages await publication.
