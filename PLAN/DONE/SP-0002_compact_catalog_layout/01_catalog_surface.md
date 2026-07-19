# Phase 01: Catalog surface

**Produces:** responsive catalog cards, labelled search/facets, search reset.

**Status:** Completed

1. Update `src/StreamsPlayer.App/MainWindow.xaml` to label the search and each facet; add an accessible Clear action; replace the row template with a compact, responsive card template whose title, status, pin and Play controls share its header.
   - Static check: every existing filter control, action, and display binding remains present; visible text includes all required labels and `Clear`.
2. Update `src/StreamsPlayer.App/MainWindow.xaml.cs` to clear the query and calculate a readable card column count after load and resize.
   - Static check: Clear assigns an empty query; resize recalculates the grid column count; no Core files change.
