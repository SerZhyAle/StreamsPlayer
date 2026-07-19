# Phase 01 — Shared glyph library

1. [x] Add local WPF vector glyph resources for language, list, grid, refresh,
   add, clear, pin, play, more, stop, confirm, cancel, fullscreen, and close.
   Add a reusable glyph-plus-label button content template/style that inherits
   the parent button foreground.
   - Static check: `App.xaml` declares reusable glyph presentation styles
     without a package reference. PASS.
