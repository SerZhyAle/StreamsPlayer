# Phase 04 — Minimum-bitrate filter

**Produces:** a Min-bitrate filter control that combines with search + facets,
persists across sessions, and visibly indicates when it is active.
**Consumes:** Phase 02 `StreamBitrate.MeetsMinimum`.

## Steps

1. [Models.cs](../../src/StreamsPlayer.Core/Models.cs) `CatalogState`: add
   `public string CatalogMinBitrateFilter { get; init; } = "All";` (older state
   files deserialize the missing key to the initializer default).
2. [MainWindow.xaml](../../src/StreamsPlayer.App/MainWindow.xaml) filter bar: add
   a new column + `StackPanel` with a `TextBlock` (`MinBitrate`) and
   `ComboBox x:Name="MinBitrateFilter"` (`SelectionChanged="FilterChanged"`).
   Populate items in code (Phase step 3) as `UiOption`s: `All`, `64`, `128`,
   `192`, `256`, `320` (values are the kbps threshold as string; labels via
   `BitrateValue` for the numeric ones, `AllOption` for All). Active indicator:
   bind a style trigger so a non-`All` selection shows an accent border/bold
   (mirrors the "mark the filter control when active" rule).
3. [MainWindow.xaml.cs](../../src/StreamsPlayer.App/MainWindow.xaml.cs):
   populate `MinBitrateFilter` in `UpdateLocalizedOptions` (where the other fixed
   option combos are localized — verify exact method) so relabeling on language
   switch works. In `ApplyFilter`, read
   `var minBitrate = SelectedOptionValue(MinBitrateFilter) ?? AllValue;` and add
   to the `Where` predicate: `(minBitrate == AllValue ||
   StreamBitrate.MeetsMinimum(channel.Bitrate, int.Parse(minBitrate)))`.
   Rows with missing/unparseable bitrate fall out only when a minimum is active
   (AC3); the default `All` leaves the view exactly as before (AC2/default
   constraint).
4. [MainWindow.BrowsingSession.cs](../../src/StreamsPlayer.App/MainWindow.BrowsingSession.cs):
   in `RestoreBrowsingSession` add
   `SelectOptionValue(MinBitrateFilter, _state.CatalogMinBitrateFilter, AllValue);`
   and in `SaveBrowsingSessionAsync` add
   `CatalogMinBitrateFilter = SelectedOptionValue(MinBitrateFilter) ?? AllValue`.
5. Localization: add `MinBitrate` (Min bitrate / Мин. битрейт),
   `MinBitrateTip`, `MinBitrateName` to both dictionaries.

## Notes

- The predicate is ANDed with the existing search/category/language/country/media
  clauses, so it composes predictably (AC2). `int.Parse` is safe because option
  values are the fixed numeric strings above.
- `FilterChanged` already calls `ScrollToCatalogStart()` +
  `ScheduleBrowsingSessionSave()`, so the new combo participates without extra
  wiring.

## Static check

`dotnet build src/StreamsPlayer.App -c Debug`
expected: App builds; XAML compiles | actual: Build succeeded, 0 warnings, 0 errors
