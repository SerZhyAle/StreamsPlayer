# Research — SP-0007 Button glyphs

## Evidence

- `MainWindow.xaml` declares 14 actionable buttons: top-bar controls, search
  clear, card/grid actions, empty-state actions, and audio stop.
- `AddStreamWindow.xaml` declares Cancel and Add; `PlayerWindow.xaml` declares
  Fullscreen and Close.
- Existing action labels are localized in `Localization.en.xaml` and
  `Localization.ru.xaml`; button tooltips and automation names already carry
  the accessible action descriptions.
- The present UI uses text-only actions and Unicode refresh, plus, play,
  overflow, and star symbols, which are not a coherent icon system and can
  fall back to emoji-like fonts.
- The App project is a WPF `net10.0-windows` executable with no icon-library
  dependency. WPF `DrawingImage` resources can provide local DPI-independent
  vector images without adding a dependency or image files.
- `ChannelRow.PinGlyph` and `ChannelRow.StatusGlyph` only supply Unicode
  visual symbols to the main-window templates. The status can retain its
  meaning with a colour marker and existing tooltip/automation label.
- `rg` found no emoji-range characters in `docs/` or the three README files.

## Decision

Use a small, consistent local `DrawingImage` glyph library in application
resources. Every `Button` will render a glyph plus its existing localized
label; compact card/tile controls will render a glyph-only image while keeping
their existing tooltip and automation name. No emoji or icon-font characters
will remain in the App or documentation.

## Constraints and risks

- The visible controls must retain their current commands, localization,
  disabled states, tooltips, and automation names.
- Accent/dark-surface buttons need glyphs that inherit the button foreground.
- Pin state needs distinct pinned and unpinned vector images and must update
  when the existing binding context is refreshed.
