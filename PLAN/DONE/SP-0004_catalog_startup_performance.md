# SP-0004: Faster cached catalog and compact tiles

**Status:** Verified

## Goal

Show a saved catalog promptly, make ongoing catalog work visible, and reduce visual and rendering overhead in the tile grid.

## Why

The catalog is already saved locally, but preparing every favicon before any card is rendered creates unnecessary startup and filter work. The current cards also have excess height and gaps.

## Non-goals

- Change catalog refresh policy or add automatic downloads.
- Change persisted catalog format, stream data, playback, or filter semantics.
- Claim a numeric completion percentage for unknown network work.

## Constraints

- The local state and matching atlas remain the only startup cache; atlas versions must not be mixed.
- Missing or invalid favicon tiles remain safe and blank.
- Cards retain visible title, media kind, URL, metadata, status, pin, and Play actions.
- Progress is accessible and truthful.

## Acceptance criteria

1. A saved local catalog starts without a network request and displays a visible preparation indicator while state is loading.
2. Remote refresh displays a visible, accessible in-progress indicator until it completes or fails.
3. A favicon is decoded/cropped only when its card is rendered, then is reused from the existing cache; a refreshed atlas invalidates the old tile cache.
4. Applying search, filters, or sort does not eagerly create favicon images for all matching channels.
5. Cards are materially shorter, top-aligned, and separated by small consistent gaps while preserving all existing stream details and actions.
6. Release build succeeds and the running WPF app demonstrates cached catalog display, activity indication, compact wide/narrow cards, and correct search reset.

## Risks

Lazy imagery and visual progress affect the main browse path, so both no-favicon handling and actual WPF observation are required.

## Research

See [research dossier](SP-0004_catalog_startup_performance/research.md).

## Last Audit

- PASS — `StreamCatalogStore.LoadAsync` remains the sole startup catalog source; no startup network request was introduced.
- PASS — expected: no eager favicon creation in filtering | actual: static search found `FaviconTileLoader.Load` only in `ChannelRow.Favicon`; `ApplyFilter` creates or reuses presentation rows without loading images.
- PASS — expected: cache invalidates for changed atlas/channel data | actual: `ApplyFilter` clears the row cache when atlas path changes and `GetOrCreateRow` replaces a row unless all channel and atlas inputs match.
- PASS — expected: activity is visible before local or remote I/O | actual: startup and refresh set busy state, render the dispatcher frame, and show the named indeterminate `CatalogProgress`; a live refresh also disabled duplicate Update activation during its operation.
- PASS — expected: compact cards retain stream information/actions | actual: observed 3-column running WPF catalog has 84px cards, 3px gaps, title/status/pin/Play, URL, and media/topic/country/language metadata; evidence: `tmp/streamplayer-catalog-perf.png`.
- PASS — expected: build and tests pass | actual: `dotnet build StreamPlayer.sln -c Release` succeeded with 0 warnings/errors; `dotnet test StreamPlayer.sln -c Release --no-build` passed 26/26.
