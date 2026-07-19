# Phase 01: Lazy catalog presentation

**Produces:** viewport-driven favicon creation and reusable presentation rows.

**Status:** Completed

1. Update `src/StreamPlayer.App/MainWindow.xaml.cs` so a channel row holds the atlas coordinates and lazily resolves its image when WPF reads the favicon binding; reuse a row only when its channel/atlas inputs still match.
   - Static check: `ApplyFilter` does not call `FaviconTileLoader.Load`; `ChannelRow.Favicon` is the only call site.
2. Clear or replace cached presentation rows when channel or atlas inputs change.
   - Static check: refresh and changed channel state cannot retain a row bound to a prior atlas or channel value.
