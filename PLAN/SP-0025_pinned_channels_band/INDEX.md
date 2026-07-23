# SP-0025 Tactical Plan — Collapsible pinned-channels section

**Strategic spec:** `PLAN/SP-0025_pinned_channels_band.md` (Approved)

## Architecture baseline (from research)

- One `ListView StreamsList` bound to `ObservableCollection<CatalogGridRow> GridRows`; each
  `CatalogGridRow` is `Rows.Chunk(_catalogColumns)` laid out by a `UniformGrid`. Grid-vs-list is a
  per-cell `DataTemplate` swap driven by the window-level `IsGridMode`.
- Pinned channels are today only *floated to the front* of the single `Rows` list in `ApplyFilter`
  (`pinned.Concat(unpinned)`), and pinned currently ignore the active sort (ordered by `SortIndex`).
- Previews enumerate visible tiles via `GetVisibleRows()` over `StreamsList`/`GridRows` only.
- UI state persists on the init-only `CatalogState` record (`_store.SaveAsync(_state with { … })`).

## Deliberate behavior change

Per spec constraint "active sort orders both sections", the pinned section now honours the shared
sort (`SortUnpinned` applied to pinned too), replacing the current `SortIndex`-only pinned ordering.
Newest-pin-to-front `SortIndex` bookkeeping in `PinButton_Click` stays (harmless; only affects the
`SortIndex` sort would-be, which is not a user sort option).

## Phases (dependency-ordered)

### Phase 1 — Core: persist collapse state
- File: `src/StreamsPlayer.Core/Models.cs`.
- Add to `CatalogState`: `bool PinnedSectionCollapsed { get; init; }` and
  `bool MainSectionCollapsed { get; init; }` (default `false` = expanded; old state deserializes to default).
- **Check:** `dotnet build src/StreamsPlayer.Core -c Debug` succeeds.

### Phase 2 — Localization keys (en + ru, no emoji)
- Files: `Localization.en.xaml`, `Localization.ru.xaml` (keep 1:1 parallel).
- Add: `PinnedSectionHeader` ("Pinned"/"Закреплённые"), `MainSectionHeader` ("Channels"/"Каналы"),
  `CollapseSectionTip` ("Collapse section"/"Свернуть раздел"),
  `ExpandSectionTip` ("Expand section"/"Развернуть раздел").
- **Check:** both files parse (build); key count matches between files.

### Phase 3 — XAML: two collapsible sections in Grid.Row 2
- File: `src/StreamsPlayer.App/MainWindow.xaml`.
- Replace the single-`Grid` content of the Row-2 `Border` with a 4-row grid:
  `Auto` pinned header, `Auto` pinned content, `Auto` main header, `*` main content.
- Pinned header/main header: clickable `Border` (`PinnedHeader_Click` / `MainHeader_Click`) with a
  section title (`DynamicResource`) and a chevron `Path` (down expanded / right collapsed via a
  DataTrigger on the bound collapse property).
- Pinned content: `ScrollViewer x:Name="PinnedScroll"` hosting `ItemsControl x:Name="PinnedItems"`
  bound to `PinnedRows`. ItemsPanel switches by `IsGridMode`: grid → `WrapPanel` (fixed-size tiles
  wrap, vertical scroll, `MaxHeight` cap); list → horizontal `StackPanel` (fixed-width cards, sideways
  scroll, fixed height). Cell template reuses the existing `StreamGridTileTemplate` /
  `StreamCardTemplate` via a `ContentControl` `IsGridMode` trigger; wire `MouseDoubleClick` →
  `StreamsList_MouseDoubleClick`.
- Main content: the existing `ListView StreamsList` + `EmptyPanel`, unchanged internally (keep its own
  virtualizing scroll — do NOT wrap it in an outer ScrollViewer).
- Bind visibilities to window props (`PinnedHeaderVisibility`, `PinnedContentVisibility`,
  `MainContentVisibility`).
- **Check:** `dotnet build src/StreamsPlayer.App -c Debug` succeeds (XAML compiles).

### Phase 4 — Code-behind: split pipeline + collapse + previews
- New partial `src/StreamsPlayer.App/MainWindow.PinnedSection.cs` (keep `MainWindow.xaml.cs` from growing):
  - `ObservableCollection<ChannelRow> PinnedRows { get; }`.
  - INPC props: `PinnedSectionCollapsed`, `MainSectionCollapsed`, and computed
    `HasPinned`, `PinnedHeaderVisibility`, `PinnedContentVisibility`, `MainContentVisibility`,
    `PinnedChevronCollapsed`/`MainChevronCollapsed` (or reuse the collapse bools for the chevron trigger).
  - `PinnedHeader_Click` / `MainHeader_Click`: toggle the bool, persist via
    `_store.SaveAsync(_state with { … })`, raise PropertyChanged, call `UpdatePinnedSectionLayout()`.
  - `UpdatePinnedSectionLayout()`: set `PinnedScroll` sizing imperatively — list mode: fixed `Height`,
    `HorizontalScrollBarVisibility=Auto`, vertical disabled; grid mode: `Height=Auto`,
    `MaxHeight = MainSectionCollapsed ? large : contentArea*fraction`, vertical Auto, horizontal disabled.
- Modify `ApplyFilter` (`MainWindow.xaml.cs`): populate `PinnedRows` from `SortUnpinned(pinned)` and
  `Rows`/`GridRows` from `SortUnpinned(unpinned)`; empty-state uses `PinnedRows.Count + Rows.Count`;
  status count = `PinnedRows.Count + Rows.Count`; refresh `HasPinned`-driven visibilities + layout.
- Modify `GetVisibleRows` (`MainWindow.Previews.cs`): when `HasPinned && !PinnedSectionCollapsed && IsGridMode`,
  prepend all `PinnedRows`; return distinct-by-URL.
- Load in `MainWindow_Loaded`: seed collapse bools from `_state` before `ApplyFilter`; call
  `UpdatePinnedSectionLayout()`. Hook the Row-2 container `SizeChanged` to `UpdatePinnedSectionLayout`.
- **Check:** `./build.ps1 -Test` (Debug build + tests) passes.

### Phase 5 — Run-and-observe (spec AC #8)
- `./run.ps1`; observe with ≥1 pinned channel:
  1. Pinned appear only in pinned section; unpinned only below. `expected|actual`.
  2. Collapse/expand each section; restart → state persists.
  3. Scroll main list → pinned section stays anchored.
  4. Unpin last pinned → section hidden, main uses full height.
  5. Apply a filter + change sort → both sections update consistently.
  6. Toggle grid/list → both switch together; pinned wraps (grid) / sideways strip (list).
  7. Pin/unpin moves a channel between sections; persists across restart.
- Record each as `expected: … | actual: …`. Set status `Implemented`, then `Verified` after observation.

**Result:** Phases 1–4 complete; build+tests green (108/108). Phase 5 observed AC1/AC2(persist)/
AC4/AC5/AC6 through real app restarts (evidence in `tmp/sp0025_*.png`, summarized in the strategic
ticket's Verification section). The three live-input gestures (header collapse click, scroll
anchoring, live pin-move) are BlockNeedUserTest — the sandbox cannot inject WPF mouse input; their
backing logic is code-verified and their rendered results observed.

## Risks / watch-items
- Nested scroll: keep `StreamsList` virtualization intact (no outer ScrollViewer around it).
- Preview double-capture across two regions: dedupe visible rows by URL.
- List-mode strip card width must be bounded so wide cards don't dominate.
