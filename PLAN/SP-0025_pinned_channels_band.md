# SP-0025: Collapsible pinned-channels section

**Status:** Implemented (BlockNeedUserTest for the three live-input gestures — see Verification)

## Goal

Give pinned channels their own section anchored at the top of the catalog, above
the main channel list, so favourites stay reachable while the user browses. Both
the pinned section and the main list are collapsible to a header. The pinned
section is sized to its contents; the main list fills the remaining height and
scrolls. Filters, sorting, and view mode apply identically to both.

## Why

Pinned channels are the ones the user returns to most. Today they merely float to
the top of the same scrolling list and disappear the moment the user scrolls down.
A dedicated, always-anchored section — that the user can collapse out of the way
when they need room — keeps favourites visible while they explore the full catalog.

## Non-goals

- No change to how a channel becomes pinned or unpinned; the existing pin action
  stays as-is.
- No drag-to-reorder inside the pinned section; ordering follows the active sort.
- No change to how the pinned flag is stored, nor to the catalog refresh /
  MANUAL/IMPORTED merge contract.
- No new view mode; grid and list remain the only two presentations.
- No separate filter or sort controls for the section; they are deliberately shared.

## Decisions

1. **Two collapsible sections.** The catalog shows a pinned section stacked above
   the main list. Each section has a header that collapses it to just the header
   and expands it again. The pinned section is content-sized (its height grows with
   the number of pinned channels); the main list fills the remaining height down to
   the bottom of the window and scrolls independently.
2. **Overflow — grows, no fixed two-row cap.** The pinned section is not capped at
   two rows; it grows with its channel count, and the user collapses it when they
   want space. To keep the main list usable, if the expanded pinned section would
   take more than a reasonable share of the window it scrolls within its own area
   rather than pushing the main list off screen.
3. **Empty state — hidden.** When nothing is pinned, the pinned section is hidden
   entirely and the main list uses the full height.
4. **Per-view layout.** Both sections use the shared view mode. In grid view the
   pinned section wraps tiles into as many rows as needed (vertical growth per
   decision 2). In list (card) view the pinned section is a horizontal strip of
   cards that scrolls sideways at a fixed height, so wide cards do not dominate the
   window.
5. **Collapse state persists.** Each section's collapsed/expanded state is
   remembered across restart.

## Constraints

- The pinned section stays anchored above the main list; scrolling the main list
  does not move it.
- The active category/language/country/media filters apply to both sections; a
  pinned channel that does not match the active filter is not shown in the pinned
  section while that filter is active.
- The active sort mode orders both sections by the same rule.
- The active view mode renders both sections in the same presentation and switches
  them together.
- Grid previews/thumbnails, tile actions, and selection behave in the pinned
  section exactly as in the main list.
- Any new text (section headers, collapse affordance) is localized in English and
  Russian; no emoji is introduced.

## Acceptance criteria

1. Pinned channels appear only in the pinned section; unpinned channels appear only
   in the main list below it.
2. Each section can be collapsed to its header and expanded again, and the
   collapsed/expanded state persists across restart.
3. Scrolling the main list does not move the pinned section.
4. The pinned section grows with the number of pinned channels; with none pinned it
   is hidden and the main list uses the full height.
5. Applying or clearing any filter, and changing the sort mode, updates both
   sections consistently.
6. Switching between grid and list view changes both sections together; the pinned
   section wraps tiles in grid view and is a sideways-scrolling card strip in list
   view.
7. Pinning a channel moves it into the pinned section and unpinning returns it to
   the main list, persisting across restart.
8. Build/tests pass and a run-and-observe check confirms: the section stays
   anchored while the main list scrolls, collapse/expand works, and both sections
   honour an active filter, sort, and each view mode.

## Risks

- With many pinned channels the pinned section can crowd the main list; the
  content-scroll cap (decision 2) and collapse must keep the main list usable on
  small windows.
- List-mode card strips and grid-mode tile wrapping are two different layouts for
  the same section; both must honour the shared filter/sort/view without drift.
- Preview capture now spans two visible regions; capture must cover both without
  double-work or contention with the player.

## Implementation

- Tactical plan: `SP-0025_pinned_channels_band/INDEX.md`.
- Core: `CatalogState.PinnedSectionCollapsed` / `MainSectionCollapsed` (init-only bools,
  default expanded; old state deserializes to default) — `src/StreamsPlayer.Core/Models.cs`.
- XAML: Row-2 catalog area split into four rows — pinned header / pinned content
  (`PinnedScroll` + `PinnedItems`) / main header / main list (`StreamsList`) —
  `src/StreamsPlayer.App/MainWindow.xaml`. Shared card/tile templates moved to `Grid.Resources`.
  Pinned panel switches WrapPanel (grid) ↔ horizontal StackPanel (list) by `IsGridMode`.
- Code: new partial `src/StreamsPlayer.App/MainWindow.PinnedSection.cs` (`PinnedRows`, collapse
  state + visibility props, header click handlers, `UpdatePinnedSectionLayout`). `ApplyFilter`
  splits pinned/unpinned into two collections, both ordered by the shared `SortChannels`
  (renamed from `SortUnpinned` — the pinned set now honours the active sort). `GetVisibleRows`
  spans both regions and dedupes by URL for preview capture.
- Localization: `PinnedSectionHeader`, `MainSectionHeader`, `CollapseSectionTip`,
  `ExpandSectionTip` added to `Localization.en.xaml` and `Localization.ru.xaml` (no emoji).

## Verification

Build + tests: `./build.ps1 -Test` → Release build succeeded, 108/108 tests passed.

Run-and-observe (framework build, real app restarts, `PrintWindow` captures under `tmp/`):

- AC1/AC6 (grid) — `tmp/sp0025_a_grid.png`: pinned channels (CT Sport, DFM) render as wrapping
  grid tiles under the "Закреплённые" header, anchored above the "Каналы" main grid.
  expected: pinned tiles above main grid | actual: matches.
- AC6 (list) — `tmp/sp0025_s2_list.png`: in List view both sections show cards; the pinned
  section is a horizontal fixed-width card strip; pinned cards show the filled pin, main cards
  the outline pin. expected: pinned = sideways card strip, both switch together | actual: matches.
- AC2 (persist) — `tmp/sp0025_s1_collapsed.png`: launched with `pinnedSectionCollapsed=true`;
  pinned header shows the collapsed (right) chevron with no tiles, main grid flows up beneath.
  expected: collapsed pinned section restored on start | actual: matches. The app writes both
  collapse flags to `catalog-state.json` (save path confirmed).
- AC4/AC5 — `tmp/sp0025_s3_filterhide.png`: search "jazz" (matches no pinned channel) hides the
  pinned section entirely; main list shows jazz matches at full height.
  expected: non-matching filter hides pinned section, filter applies to it | actual: matches.

**BlockNeedUserTest (exit: user runs the three gestures once).** Live mouse-input injection is
not possible in this sandbox — an occluding always-on-top media player intercepts real screen
clicks, and WPF ignores posted `WM_LBUTTON*` messages — so these interactive gestures were not
observed directly (their backing logic is code-verified and their results observed above):
1. AC2 gesture: click a section header collapses/expands it (result render + persistence already observed).
2. AC3: scroll the main list — the pinned section stays anchored (guaranteed by construction:
   pinned is a separate `Auto` grid row; only `StreamsList` scrolls).
3. AC7: click pin/unpin moves a channel between the two sections and persists (membership follows
   the `Pinned` flag through the unchanged pin handler → `ApplyFilter` split, already observed).
