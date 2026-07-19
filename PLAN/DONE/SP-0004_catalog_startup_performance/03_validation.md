# Phase 03: Validation

**Consumes:** completed lazy presentation, activity indicator, and compact cards.

**Status:** Completed

1. Build the solution in Release configuration.
   - Check: `dotnet build StreamPlayer.sln -c Release` exits zero.
2. Launch the app with persisted catalog state and observe activity, wide and narrow layouts, searching and Clear.
   - Check: activity is visible during startup/refresh, cards are compact, controls work, and no-favicon cards remain safe.
