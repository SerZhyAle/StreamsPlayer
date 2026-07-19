# Phase 03 — Restore and save browsing context

**Consumes:** Phase 01 fields and Phase 02 controls.

1. [Done] Restore persisted selector values and query after facets/localized options are populated, then rebuild results.
2. [Done] Debounce session saves after real query/filter/sort changes, record the first visible channel as the scroll anchor, and flush pending state when the window closes.
3. [Done] After rendering restored result rows, scroll to the saved anchor when present; otherwise keep the list at its initial position.
4. [Partial] Build, test, then run and observe the compact layout and restart restoration. Layout was observed; restart restoration awaits a run without the user's existing StreamPlayer process sharing local state.

Static check: saved values are `UiOption.Value` values; an unavailable anchor is ignored without error; only App accesses WPF scroll visuals.
