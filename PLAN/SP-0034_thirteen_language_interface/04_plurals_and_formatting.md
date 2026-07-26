# Phase 04 - Pseudo-plurals and formatting

**Status:** Approved

Decision 8. Runs before phase 06 so no string is translated thirteen times and then rewritten. The
target form already exists in the repository: the Russian and Ukrainian dictionaries solved these
same keys with a "label: count" phrasing, which needs no grammatical agreement.

1. Rephrase the six parenthesised-suffix keys in `src/StreamsPlayer.App/Localization.en.xaml` -
   `ImportPreviewSummary`, `ImportResult`, `ExportResult`, `DeleteDownloadedConfirm`,
   `DeleteDownloadedResult`, `CollectionCount` - into forms correct without agreement, matching the
   Russian phrasing already in `Localization.ru.xaml`. Also rephrase `ChannelCount`
   (`{0:N0} of {1:N0} channels`), which has the same agreement problem in Slavic and Arabic even
   though it carries no `(s)`. Adjust the ru and uk values only where the new English shape changes
   the argument order.
   Static check: `rg '\(s\)' src/StreamsPlayer.App/Localization.*.xaml` returns nothing.

2. Replace the two culture-less format calls with `LocalizationService.Format`:
   `src/StreamsPlayer.App/MainWindow.Localization.cs:128` and
   `src/StreamsPlayer.App/ChannelRow.cs:169`, both formatting `BitrateValue`. Left as they are, they
   format numbers under the OS culture rather than the selected language, which becomes visible with
   Hindi, Bengali and Arabic digit shaping.
   Static check: `rg 'string\.Format\(LocalizationService' src/StreamsPlayer.App` returns nothing.

3. Make the two non-modal snapshot sites follow a language change. `PlayerWindow` is shown with
   `Show()` (`MainWindow.xaml.cs:959`), so the picker can be used while it is open, yet
   `PlayerWindow.xaml.cs:122` composes `Title` by interpolation and `:236`/`:376` assign `WaitText`
   once. Give `PlayerWindow` a `RefreshLocalization()` that recomposes them from keys, and call it
   from `MainWindow.Localization.cs:RefreshLocalizedInterface`.
   Static check: `RefreshLocalizedInterface` reaches every open `PlayerWindow`.

4. Replace the hardcoded `" - "` title separators (`PlayerWindow.xaml.cs:122`,
   `MainWindow.Localization.cs:178`) with a keyed format string so the order can differ per language.
   Static check: `rg '" - "' src/StreamsPlayer.App` returns no title composition.
