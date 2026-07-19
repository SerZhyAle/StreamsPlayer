# SP-0002: Compact responsive catalog layout

**Status:** Verified

## Goal

Let people browse a large catalog without scanning across unused screen width by presenting streams as compact cards in as many readable columns as the window supports.

## Why

The current one-row-per-stream layout places playback controls far from a channel's identity and wastes substantial space on wide displays. Search and filter controls also lack visible labels, making their purpose unclear.

## Non-goals

- Change catalog data, filtering, sorting, pinning, playback, or refresh rules.
- Add automatic downloads or change the existing player surfaces.
- Redesign other windows.

## Constraints

- Preserve all Part G browse behaviours in `docs/specifications/streams.txt`.
- Retain a readable single-column fallback at the minimum supported window width.
- Keep actions and status usable by keyboard and assistive technology.
- Do not add dependencies or move catalog logic out of the application boundary.

## Acceptance criteria

1. The catalog uses compact cards in two or more columns when the available width permits, and adapts its column count as the window resizes.
2. Each card keeps the stream title, status, pin, and Play action together; the URL and metadata remain available without expanding the card.
3. Search and every filter/sort selector have visible, unambiguous labels.
4. Search has a visible Clear action that restores the unfiltered query state and updates results.
5. Filter, sort, pin, playback, empty state, favicon fallback, and status indicators retain their existing behaviour.
6. The app builds and the changed window is observed at both narrow and wide sizes.

## Risks

Responsive layout can compete with list virtualization and needs a real WPF observation to validate its density and resize behaviour.

## Research

See [research dossier](SP-0002_compact_catalog_layout/research.md).

## Last Audit

- PASS — `MainWindow.xaml` presents labelled search, media, category, language, country, and sort controls; Clear is visible and accessible.
- PASS — `MainWindow.xaml` keeps each card's status, title, pin, and Play controls together, with URL and metadata below.
- PASS — `MainWindow.xaml.cs` groups the catalog into width-dependent card rows while retaining virtualization on the outer list; it rebuilds only on a column-count change.
- PASS — expected: `dotnet build StreamsPlayer.sln -c Release` exits zero | actual: succeeded with 0 warnings and 0 errors.
- PASS — expected: a wide catalog shows multiple compact columns | actual: launched 2048px window displayed 3 columns; evidence: `tmp/streamsplayer-catalog-compact.png`.
- PASS — expected: a narrow window remains readable | actual: launched 920px window displayed 2 columns; evidence: `tmp/streamsplayer-catalog-compact-narrow.png`.
- PASS — expected: Clear removes the query and restores results | actual: UI Automation set a query, invoked `Clear search`, and observed an empty search value; catalog returned to 2,691 of 2,691 channels.
