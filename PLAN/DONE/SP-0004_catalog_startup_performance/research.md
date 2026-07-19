# Research: catalog startup performance and compact tiles

**Date:** 2026-07-19

## Evidence

- `StreamCatalogStore.LoadAsync` already restores the persisted catalog state and atlas filename from `%LOCALAPPDATA%\StreamsPlayer`; no download is needed to show a previously refreshed catalog.
- `FaviconTileLoader` keeps a decoded atlas and individual cropped tiles only for the process lifetime. It does cache a tile after first use, but `MainWindow.ApplyFilter` requests a favicon for every filtered channel while building `Rows`, including channels outside the viewport.
- `MainWindow.ApplyFilter` rebuilds the entire `Rows` and `GridRows` collections on every search, facet, sort, pin, and play-outcome update. The outer `ListView` virtualizes rendered rows, but eagerly-created image sources still defeat much of that benefit.
- `MainWindow.xaml` gives every card `MinHeight="114"`; the outer list container does not explicitly top-align content, so the observed cards have substantially more vertical whitespace than their three compact text lines need.
- `SetBusy` already communicates refresh activity in the footer, but startup loading has no progress affordance.

## Settled UX decisions

- A cached local catalog remains the startup source of truth and is shown without network access.
- Show an indeterminate progress indicator and a plain-language status while local state is opened and while a remote refresh is in progress. Do not invent a percentage for an operation whose work is not measurable.
- Decode/crop favicon tiles only when WPF renders a visible card; retain the existing atlas/tile cache for subsequent rendering and filtering within the process.
- Use shorter cards with tight, consistent gaps. Keep title/actions, URL, and metadata visible; do not remove stream kind, status, pinning, or Play.

## Risks

- Lazy presentation must retain graceful null-favicon behaviour and invalidate correctly when the persisted atlas changes after refresh.
- Startup progress needs to become visible before awaiting local I/O, otherwise it will not be observed.
- The compact layout must still leave enough room for touch/click targets and long stream names.
