---
name: project_theme
description: DynamicResource theming, palette dictionaries, out-of-theme overlay declarations, and WPF airspace
type: project
---

# Dynamic Theming & Visual Styling

- **DynamicResource requirement (APP-STYLE):**
  - `ThemeService` updates named brush resources in `Application.Current.Resources` at runtime.
  - All UI elements must use `{DynamicResource}` for palette brushes; any `StaticResource` permanently opts the element out of runtime theme switching.
  - System theme mode monitors `AppsUseLightTheme` and subscribes to `SystemEvents.UserPreferenceChanged` only while active, unsubscribing on exit to prevent leaks.
- **Out-of-theme declarations & lint gates:**
  - Literal colours are forbidden except in `ThemeService` / `App.xaml` palette tables or preceded by an explicit `<!-- Out of theme (APP-STYLE 5: reason) -->` declaration.
  - Enforced by `ThemeColourDeclarationTests` in CI.
- **Airspace & reparented controls overlay (SP-0072):**
  - `PlayerWindow` detaches `ControlsOverlay` and passes it to the video backend's stacked foreground window to avoid native airspace occlusion.
  - WPF property inheritance from `PlayerWindow` stops at the boundary. Inherited properties (`FlowDirection`, `DataContext`, `FontFamily`) must be set explicitly on `ControlsOverlay`.
- **Glyph button styling:**
  - Default `GlyphButton` template applies Foreground to both `Fill` and `Stroke` of `GlyphGeometry`.
  - Outline icons (like clock faces) require an explicit `ContentTemplate` with `Fill="Transparent"`.
