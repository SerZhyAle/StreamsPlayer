# Research — SP-0011

## Current flow

- `MainWindow.xaml` uses two stacked header action rows and two stacked control rows; cards are in the responsive virtualized `StreamsList`.
- `MainWindow.xaml.cs` filters from the current search text and four selectors, then groups result rows based on width. Its scroll callback currently only schedules Grid-preview work.
- `CatalogState` in `StreamsPlayer.Core/Models.cs` is the persisted local record. `StreamCatalogStore` serializes it atomically and old JSON can deserialize with defaults for added properties.
- Existing store tests prove persistence of view, localization, window, grid, and selected-channel settings.

## Reusable patterns and constraints

- `UiOption.Value` is the localization-independent selector value. Persist values, never localized labels.
- `ApplyFilter` is the single rebuild path; restore must set controls before calling it and defer scrolling until the visual list has rendered.
- A channel GUID is stable across catalog refresh merge for a matching URL, unlike a pixel offset or responsive row number.
- Core remains UI-independent: store only plain strings and a channel GUID, while visual discovery/scrolling stays in App.

## Check

Expected: existing code has no persisted search/filter/sort/scroll fields.  
Actual: `CatalogState` contains only catalog, view, language, window, grid, and last-selected state; `StreamsList_ScrollChanged` only schedules previews.
