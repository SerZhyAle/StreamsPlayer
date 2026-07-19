# Research: bilingual interface and window controls

**Date:** 2026-07-19

## Evidence

- `MainWindow.xaml`, `AddStreamWindow.xaml`, and `PlayerWindow.xaml` contain English literals for every static label, tooltip, accessible name, and window title.
- `MainWindow.xaml.cs`, `AddStreamWindow.xaml.cs`, and `PlayerWindow.xaml.cs` assign English status and MessageBox text directly.
- `CatalogState` already persists the List/Grid preference through `StreamCatalogStore`; the same local state can preserve language and window preferences without a second settings file.
- WPF `Window.Topmost` directly provides independent always-on-top behavior for each window.
- `PlayerWindow` currently has one bottom overlay and no keyboard handling. Its `WindowStyle`, `ResizeMode`, and `WindowState` can be captured and restored around a borderless maximized state.

## Settled decisions

- Use runtime WPF resource dictionaries for static strings and one lookup helper for formatted/code-path strings. Catalog data remains untranslated.
- Keep filter and sort logic keyed by invariant values while presenting localized option labels.
- Store English/Russian and both topmost defaults in `CatalogState`; the main window owns persistence and passes the player preference callback into each player window.
- Label the language switch `RU / EN` in both languages. Show checkbox labels beside it rather than relying on icons.
- Interpret the final request as requiring system decorations to be hidden in fullscreen.

