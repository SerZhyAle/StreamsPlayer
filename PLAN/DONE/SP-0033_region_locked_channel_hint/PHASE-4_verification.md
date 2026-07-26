# Phase 4 - Verification

**Consumes:** Phases 1-3.
**Produces:** run-and-observe evidence for the two UI surfaces (AC 8).

## Steps

### 4.1 Full static gate

`./scripts/check.ps1` (Release restore + build + `dotnet test`).

expected: build 0 errors, all tests pass | actual: Build succeeded, 0 Warning(s), 0 Error(s);
Test Run Successful, 220/220 passed.

### 4.2 Seeded catalog run

A live catalog refresh is not required and must not be relied on - the ~42 tagged rows are upstream
data that can change. Instead, run the app against a **sandboxed** `%LOCALAPPDATA%\StreamsPlayer`
(rename the real folder aside - see `memory/MEMORY.md`; the app resolves the folder through the
known-folder API, so setting the environment variable does not redirect it) seeded with a
`catalog-state.json` containing at least: one `GeoRestricted` catalog channel, one `Open` catalog
channel, and one `Manual` channel.

Observe and record, in both list and grid mode:

Driven headlessly via UIA (`tmp/uia/sp0033-seed.ps1`, `sp0033-shots.ps1`, `sp0033-dialog.ps1`); the
seed carries four channels - geo catalog, open catalog, pinned+geo catalog, manual - on
`*.invalid.test` hosts so nothing can reach the network. Evidence PNGs under `tmp/uia/shots/`.
The seed sets `mainWindowTopmost` because `CopyFromScreen` grabs screen pixels: the first capture
attempt photographed the editor sitting over a merely-focused app window.

- expected: the `GeoRestricted` channel shows the amber pill; the other two show nothing new | actual:
  PASS. UIA text tree for list mode: `Pinned And Geo Channel … Region-locked`, `Geo Only Channel …
  Region-locked`, while `Manual Channel` and `Open Catalog Channel` carry no such node. Confirmed
  visually in `sp0033-list-en.png` and `sp0033-grid-en.png`.
- expected: the pill is legible at Small, Medium, and Large tile size and does not overlap the pinned badge on a pinned+geo channel | actual:
  PASS. `sp0033-grid-en.png` shows the pinned+geo tile with `Pinned` above `Region-locked`, no overlap,
  and the unpinned geo tile with the pill occupying the top slot (no gap). Tile size is not a variable:
  the tile content is inside a `Viewbox` with `Stretch="Uniform"`, so the pill scales with the tile -
  the same reason the existing pinned badge needs no per-size value.
- expected: the pill's tooltip shows the hedged wording | actual: NOT OBSERVED (not an acceptance
  criterion - this was an extra check). Two automation attempts failed: `SetCursorPos` teleports without
  an input event so WPF never raises `MouseEnter`, and `SendInput` with `MOUSEEVENTF_ABSOLUTE` normalizes
  against `GetSystemMetrics`, which reports logical pixels to this non-DPI-aware host while
  `BoundingRectangle` reports physical - so the pointer lands off-target. What *is* established:
  `ToolTip` binds `RegionRestrictedTip`, and its sibling `RegionRestrictedLabel` - same property pattern,
  same resource lookup - was observed rendering correctly in all three languages. A human hover would
  close this in two seconds.

### 4.3 Failure dialog

Trigger a playback failure on the seeded `GeoRestricted` channel (an unroutable host is sufficient -
the hint is gated on the tag, not on the error category), then on the `Open` channel.

- expected: the geo channel's dialog shows the region explanation and the dialog grows to fit it | actual:
  PASS on the explanation. Dialog text nodes: `"Geo Only Channel" could not be played. || The catalog
  marks this channel as region-locked: it did not respond from the maintainer's country. It may work if
  you are in the region it broadcasts to. || Retry || Copy report || Hide || Keep`
  (`sp0033-dialog-geo.png`, nothing clipped).
  **Growth was not exercised:** both dialogs measured 315 device px, i.e. `MinHeight="180"` at this
  display's scaling. The hint fits inside the existing minimum, so `SizeToContent="Height"` is currently
  inert - it is a guard against a longer translation, not an observed behaviour.
- expected: the open channel's dialog is unchanged from today | actual: PASS. Text nodes:
  `"Open Catalog Channel" could not be played. || Retry || Copy report || Hide || Keep` - no region
  node, identical geometry (`sp0033-dialog-open.png`).

### 4.4 Localization

Switch language to Russian and Ukrainian with the geo channel visible.

- expected: pill, tooltip, and failure hint are localized in all three languages, no emoji, no clipped text | actual:
  PARTIAL-BY-DESIGN. The **pill** was observed in all three: `Region-locked` (en), `Только для региона`
  (ru, `sp0033-list-ru.png`), `Лише для регіону` (uk, `sp0033-grid-uk.png`) - no clipping, no emoji.
  The **failure hint** was observed in English and Russian (`sp0033-dialog-ru.png`): `Каталог отмечает
  этот канал как доступный только в своём регионе: из страны составителя он не отвечал. Он может
  работать, если вы находитесь в регионе вещания.` The Ukrainian string is present in the dictionary and
  resolves through the same `DynamicResource` path the pill already exercised in Ukrainian.

### 4.5 Backward compatibility

Load a `catalog-state.json` captured **before** this change (no `Access` property on any channel).

- expected: loads without error, every channel renders as `Open`, no migration prompt or log | actual:
  PASS. A state file with no `access` property on its channel loaded cleanly; UIA text tree shows
  `Legacy No Access Property … VIDEO` with no region node and no dialog (`sp0033-legacy.png`). Covered
  again at unit level by `Save_RoundTripsChannelAccessAndDefaultsLegacyStateToOpen`.

## Exit

All checks recorded with actuals. Every acceptance criterion is observed or accounted for; the two
qualified items (tooltip hover, RU/UK failure-hint rendering) are binding-level rather than
pixel-level checks and are stated as such above rather than claimed as observations.
