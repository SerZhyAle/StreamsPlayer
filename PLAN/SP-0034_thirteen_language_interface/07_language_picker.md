# Phase 07 - The language picker

**Status:** Approved

Decision 6 and criterion 7. Today the picker is a checkable `ContextMenu` rebuilt on every open,
behind a two-letter badge (`MainWindow.xaml:30-36`, `MainWindow.Localization.cs:17-61`). Thirteen flat
checkable items do not scale, and the badge is ambiguous - the Ukrainian ISO code reads as
"United Kingdom".

1. Add `src/StreamsPlayer.App/LanguageWindow.xaml` and `.xaml.cs`: a modal selection surface listing
   `InterfaceLanguages.All` by endonym, one row each, with the active language visibly marked. Use a
   `ListBox` so arrow keys, Home/End and type-to-select work without custom key handling; `Enter`
   and double-click commit, `Escape` cancels. Set `AutomationProperties.Name` on the list and on each
   row from the localized endonym, and give the window a localized title. Keep the file under the
   ~500-line budget; it should be well under.
   Static check: the window binds `InterfaceLanguages.All` and declares no per-language literal.

2. Replace the badge in `src/StreamsPlayer.App/MainWindow.xaml:30-36`: keep the toolbar button and its
   `LanguagePickerName` automation name and tooltip, drop the nested `ContextMenu`, and drop the
   two-letter `Content`. Use the existing `LanguageGlyphButton` style with a glyph rather than text,
   so no layout width depends on the language code.
   Static check: `MainWindow.xaml` contains no `LanguageMenu` and no two-letter content.

3. Rewrite `MainWindow.Localization.cs:17-61`: `LanguageButton_Click` opens `LanguageWindow` with
   `ShowDialog`, and on a confirmed change applies, persists and refreshes exactly as
   `LanguageMenuItem_Click` does today. Delete `BuildLanguageMenu`, `LanguageMenuItem_Click` and
   `UpdateLanguageButton`.
   Static check: `rg 'BuildLanguageMenu|ShortCode' src/StreamsPlayer.App` returns nothing.

4. Keep `RefreshLocalizedInterface` as the single fan-out and extend it with the `PlayerWindow`
   refresh added in phase 04, so a change made while a non-modal player is open is visible there too.
   Static check: the fan-out is called once per confirmed change.

5. Do not gate the button on `_preferencesLoaded` alone. Phase 02 makes a failed load impossible for
   an unknown language, but if a load still fails the user must be able to pick a language rather than
   face a disabled control with no recovery.
   Static check: the button's enabled state does not depend on a successful catalog load.
