# Phase 02 — Button adoption

1. [x] Update all buttons in `MainWindow.xaml`, `AddStreamWindow.xaml`, and
   `PlayerWindow.xaml` to use the shared glyph presentation; use glyph-only
   compact card/tile controls with their existing accessible descriptions.
   Replace the Unicode pin, playback, overflow, refresh, add, and empty-state
   visuals.
   - Static check: every `<Button` in those windows has glyph content and no
     Unicode icon character or `PinGlyph`/`StatusGlyph` binding remains. PASS.
2. [x] Remove obsolete Unicode-glyph presentation members from `ChannelRow` and
   preserve the existing textual status description and colour semantics.
   - Static check: no production source references `PinGlyph` or `StatusGlyph`.
     PASS.
