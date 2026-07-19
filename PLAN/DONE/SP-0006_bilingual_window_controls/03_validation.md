# Phase 03: Validation and audit

**Status:** Completed

1. Run focused persistence tests and the complete Release check.
   - Static check: commands exit zero with zero warnings.
2. Automate the main and player windows to observe EN/RU replacement, independent topmost toggles, fullscreen system-chrome removal, Escape restoration, and restart persistence.
   - Static check: evidence under `tmp/` records properties and screenshots in both languages/states.
3. Audit every strategic criterion and update ticket status from evidence.
   - Static check: no failed or open manual criterion remains before `Verified`.

## Evidence

- `scripts/check.ps1`: expected zero errors/warnings and all tests passing | actual zero errors, zero warnings, 28/28 tests passed.
- Resource parity: expected identical EN/RU keys | actual 99/99 keys, zero differences.
- GUI automation: expected live language replacement, independent topmost, restart persistence, borderless fullscreen, Escape/F11 restoration | actual every recorded assertion returned `true`; fullscreen measured 3840 x 2160.
- Final local preference state: expected automation cleanup | actual English selected, main/player topmost false, zero StreamsPlayer processes.
