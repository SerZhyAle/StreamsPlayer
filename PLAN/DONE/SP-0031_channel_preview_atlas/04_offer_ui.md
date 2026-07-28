# Phase 4 - Offer UI, wiring, localization

Consumes Phase 3. Implements the settled option (c) and AC 1, 6, 8.

## Steps

1. **Offer state in `MainWindow.Previews.cs`.**
   - `private bool _previewAtlasOfferLatched;` - **in-memory only**, per session.
   - Eligibility: `_state.ChannelPreviewAtlasRevision != ChannelPreviewAtlasService.Revision`
     `&& !_previewAtlasOfferLatched`.
   - Latch on **accept only**. Declining leaves the latch clear so the next catalog update offers again -
     this is both the spec's re-offer rule and the user's way back after a decline (mobile lesson: only an
     accepted offer latches).

2. **Offer affordance in `MainWindow.xaml`.** A single-line bar directly above the catalog area, collapsed
   by default, reusing existing brushes/styles: a short message plus a **Download** button and a **Not now**
   button. Deliberately not a modal dialog and not the transient status line - the mobile equivalent had to
   be changed to an indefinite affordance because a timed one shown behind the "catalog updated" toast was
   missed entirely.
   - Localized keys in `Localization.{en,ru,uk}.xaml`: `ChannelPreviewsOffer`, `ChannelPreviewsOfferTip`,
     `ChannelPreviewsDownload`, `ChannelPreviewsNotNow`, `ChannelPreviewsWorking`, `ChannelPreviewsDone`,
     `ChannelPreviewsUnavailable`. Full parity across all three files.
   - Static check: grep each key in all three dictionaries - expected: 3 hits per key.

3. **Show it after a catalog update.** At the end of the success path of `RefreshButton_Click`
   (`MainWindow.xaml.cs`), call `MaybeOfferChannelPreviews()`. Nothing else may call the download.
   - Static check: grep `DownloadChannelPreviewsAsync` - expected: exactly one call site, the accept handler.

4. **Accept handler.** Hides the bar, latches, shows `ChannelPreviewsWorking` with progress on the status
   line, keeps the window responsive (the import is already on a worker thread), then:
   - success -> `ChannelPreviewsDone` with the seeded count;
   - `CodecUnavailable` -> `ChannelPreviewsUnavailable`, plain wording, no stack trace, no dialog (**AC 6**);
   - network/parse failure -> the existing catalog-failure status treatment; the revision marker is **not**
     written, so the offer returns on the next catalog update.

5. **Refresh the grid after a successful import (AC 1).** Call `await QueueVisibleSafelyAsync(force: false)`
   so the store-backed tiles load into `PreviewFrameCache` and `ApplyPreview` repaints the visible rows.
   Without this the grid keeps showing favicons until the view is rebuilt - the exact bug reported on the
   mobile side when the atlas landed after the screen cached its map.
   - Static check: `./build.ps1 -Test` - expected: build succeeds and the Core suite stays green.

## Stop conditions

- The bar's placement and wording are the one genuinely user-facing choice here. If it crowds the toolbar or
  reads badly at runtime, stop and route the wording/placement through `/streamsplayer-ui-clarify` rather
  than guessing a second time.
