# Research: compact catalog layout

**Date:** 2026-07-19

## Evidence

- `tmp/streamsplayer-catalog.png` shows a single full-width row per stream. Its pin and Play controls are separated from the title by the unused row width; the filter controls display values only, and the search field has no visible label or reset affordance.
- `src/StreamsPlayer.App/MainWindow.xaml` owns the catalog, search, facet selection, and row controls in one `ListView`; its container is already scrollable and virtualized.
- `src/StreamsPlayer.App/MainWindow.xaml.cs` applies filtering on `TextChanged` and `SelectionChanged`, so an explicit clear control can reuse `FilterChanged` by assigning an empty search value.
- `docs/specifications/streams.txt`, Part G, requires all category/language/country/media-kind filters, sorting, searching, pinning, outcome status, and favicon fallback to remain usable.

## Current flow and reusable patterns

`Rows` is an observable collection of `ChannelRow` view models. The list template binds status, favicon, title, URL, metadata, pin glyph, and Play action directly. Existing global button and ComboBox styles establish spacing and can be overridden locally where denser controls are needed.

## Settled UX decisions

- Use compact cards arranged in a responsive grid, with a minimum card width so the column count increases only when cards remain readable.
- Keep pin and Play on the card's title line, immediately adjacent to stream identity.
- Preserve all displayed stream state and actions, but reduce the URL to a secondary one-line detail.
- Add visible labels: `Search catalog`, `Media`, `Category`, `Language`, `Country`, and `Sort by`.
- Provide a visible `Clear` button beside search. It clears the query and re-applies the existing filters.
- Keyboard and screen-reader names remain explicit for search, Clear, facets, and each existing action/status.

## Risks

- A multi-column panel must recompute its count on resize and keep each card sufficiently wide for long titles and actions.
- The catalog can contain thousands of rows; preserve WPF virtualization settings where compatible with the new layout.
- The visual change needs a launched WPF observation; static/build checks alone cannot verify spacing at wide and narrow window sizes.
