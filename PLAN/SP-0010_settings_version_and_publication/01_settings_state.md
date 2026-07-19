# Phase 01: Settings contract and window

**Status:** Completed

1. Extend `CatalogState` in `src/StreamsPlayer.Core/Models.cs` with a tile-size enum and a default-enabled preview preference; extend `StreamCatalogStoreTests` with non-default round-trip coverage.
   - Check: Large and disabled survive save/load, while a new state defaults to Medium and enabled.
2. Add `SettingsWindow.xaml`, code-behind, and product/version helpers under `src/StreamsPlayer.App`.
   - Check: the modal exposes localized grid settings, assembly-derived version, authorship, and all required web destinations without hardcoded display-version text.
3. Extend both localization dictionaries and `App.xaml` with Settings strings and a local vector settings glyph.
   - Check: EN/RU dictionaries have identical keys and the Settings button meets the existing glyph convention.
