# Phase 01: Localization and preferences

**Status:** Completed

1. Extend `CatalogState` with English/Russian, main topmost, and player topmost preferences and cover JSON round trips.
   - Static check: non-default Russian and both true values survive save/load.
2. Add complete English and Russian WPF string dictionaries plus a runtime lookup/switch service.
   - Static check: both dictionaries expose identical keys and replacing the active dictionary raises a language-change signal.
3. Replace main, add-dialog, player, status, message, filter, sort, and accessibility literals with localized resources/lookups.
   - Static check: remaining English UI literals are limited to product/data/protocol names or invariant internal keys.
