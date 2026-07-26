# SP-0031: Channel preview pictures from the published atlas

**Status:** Implemented - all five phases done and verified in the sandbox on the live published assets.
AC 1, 2, 3, 5, 7, 8 pass by run-and-observe; AC 4 and AC 6 are still unproven (see Verification).

Tactical plan: [SP-0031_channel_preview_atlas/INDEX.md](SP-0031_channel_preview_atlas/INDEX.md)
Research dossier: `temp/SP-0031/RESEARCH.md` - evidence under `temp/SP-0031/verify/`.

## Implementation notes

- `src/StreamsPlayer.Core/ChannelPreviewAtlas.cs` (new) - tile geometry, `RectFor`, `IsInBounds`.
- `src/StreamsPlayer.Core/ChannelPreviewCoords.cs` (new) - sidecar parser, skips malformed entries.
- `src/StreamsPlayer.Core/ChannelPreviewAtlasService.cs` (new) - the two asset URLs, `Revision`, download
  with a size ceiling; sidecar fetched first so a broken publish fails before the sheet.
- `src/StreamsPlayer.Core/Models.cs` - `CatalogState.ChannelPreviewAtlasRevision`.
- `src/StreamsPlayer.App/ChannelPreviewAtlasImporter.cs` (new) - decode once, crop, seed, trim once.
- `src/StreamsPlayer.App/PreviewFrameStore.cs` - `Exists`, synchronous `Write`/`TrimOnce` bulk path.
- `src/StreamsPlayer.App/MainWindow.ChannelPreviews.cs` (new) - offer state, accept/dismiss, import,
  dedicated `HttpClient`, post-import grid refresh and memory reclaim.
- `MainWindow.xaml` + `.xaml.cs` - offer bar above the catalog, shown only after a catalog update.
- `Localization.{en,ru,uk}.xaml` - eight keys at full parity.

## Regression reported by the owner after first use, and its fix

**Symptom:** previews were filling in, then after watching one channel the grid fell back to favicons and
never recovered.

**Root cause (from `PREVIEW COORD`/`PREVIEW VISIBLE` diagnostics added for this):**

```
PREVIEW COORD   | state=started
PREVIEW VISIBLE | rows=15 | restored=13
PREVIEW COORD   | state=stopping      <- 100 ms later
PREVIEW COORD   | state=stop_noop     <- and never started again
```

Restoring a stored preview from disk was gated on the *capture* session. Capture is deliberately suspended
whenever the window is inactive or a stream plays, so watching one channel stopped the coordinator - and
with it every repaint. `ScheduleVisiblePreviewUpdate` also bailed on `IsRunning != true`, so scrolling
could not repaint either. Before this feature almost no tiles had a stored preview, so the gate was
invisible; with ~1900 stored it strips the whole grid.

**Fix:** separate showing from capturing.
- `GridPreviewCoordinator.QueueVisibleAsync` restores and applies stored frames with or without a session,
  and only *enqueues captures* when one exists.
- `MainWindow.ScheduleVisiblePreviewUpdate` no longer requires `IsRunning`.
- `RefreshPreviewsButton_Click` starts a session before forcing a capture pass (a forced queue with no
  session used to be a silent no-op - this is also why the first AC 4 attempt captured nothing).
- `PreviewFrameCache` capacity 64 -> 192: each eviction blanks a tile until it scrolls back, and 64 is
  under one viewport once the pinned band is expanded. Measured `evictions=0` across a deep scroll.
- `GridPreviewFeature.CaptureEnabled` is `static readonly`, not `const`: as a const the compiler folded
  every guard into unreachable code.

Verified: scrolling deep through 3687 channels with the coordinator stopped now emits a `PREVIEW VISIBLE`
per step restoring 3-24 tiles, and the grid stays populated (`temp/SP-0031/verify/scroll-after-fix.png`).

## Two defects found by run-and-observe that a green build had hidden

1. **Hard crash after exactly one tile.** `CroppedBitmap.Freeze()` reaches through to
   `BitmapDecoder.IsDownloading`, and a decoder stays thread-affine even when the frame is frozen. The
   first `await` inside the seed loop moved the continuation to another pool thread and the next crop
   threw `InvalidOperationException`, which escaped an `async void` handler and killed the app. Fixed by
   making the whole import synchronous on one thread (`Import`, not `ImportAsync`) with a synchronous
   store write; the handler also now catches `InvalidOperationException` so no imaging fault can ever
   take the app down again.
2. **AC 5 failed on the first pass:** 786 MB resident after the import against 289 MB before. The decoded
   sheet is a ~235 MB WIC bitmap released through a finalizer, so nothing reclaimed it. Fixed with an
   explicit post-import reclaim, scoped so the compressed payload is unreachable first. Re-measured: 348 MB
   after against 346 MB before.

## Verification (sandbox, live assets, 2026-07-26)

| AC | Check | expected \| actual |
| --- | --- | --- |
| 8 | offer at startup | absent \| absent; no asset request before acceptance |
| 8 | offer after "Update catalog" | shown \| shown (`temp/SP-0031/verify/02-after-update.png`) |
| 1 | grid after accepting | real broadcast frames \| yes (`verify/03-grid-seeded.png`) |
| 1 | seeded count | ~1876 of 2072 video \| `seeded=1864 skipped_existing=12 not_in_catalog=5 out_of_bounds=0`, 1876 files |
| 2 | audio channels | untouched \| 0 non-video tiles in the sidecar; audio rows keep favicons |
| 3 | captured frames preserved | never overwritten \| 12 pre-existing captures skipped |
| 5 | memory returns | back to pre-import \| 348 MB vs 346 MB before |
| 7 | revision persisted | `v1` \| `v1`; offer does not return |
| - | store size | inside the 150 MB budget \| 11.6 MB / 1878 files |
| - | duration | tolerable \| ~3 s for 1864 tiles |

**Not yet verified - why this is Implemented and not Verified:**

- **AC 4** (a live "Refresh previews" capture overrides a seeded tile and survives a restart) - not exercised.
- **AC 6** (a machine without the WebP WIC codec degrades to a plain message) - cannot be produced on this
  machine, which has the codec. Needs either a machine without it or an injected decode failure.
- The offer bar's placement and wording have only been seen at one window size.

Research dossier: `temp/SP-0031/RESEARCH.md` (measured contract, coverage, and the decode constraint).

## Goal

Let a user get real preview pictures for video channels straight from the internet, instead of waiting for
the app to connect to each stream and grab a frame itself. The catalog publisher already produces a single
sheet of ready-made per-channel frames; StreamsPlayer should be able to fetch it and fill its grid from it.

## Why

Today every preview in the grid is earned the hard way: the app opens the live stream, waits for a first
frame, and saves it. That only happens for tiles currently on screen, only while nothing is playing, and only
for channels that answer right now - so a fresh install shows a wall of tiny favicons and fills in slowly,
channel by channel, over many sessions. Channels that are temporarily down never get a picture at all.

The publisher already solved this offline: a sheet of frames covering **1876 of the 2072 video channels
(90.5%)** in the current catalog, 11 MB for the lot. The mobile app consumes it; the Windows app does not,
so the same catalog looks far emptier on Windows than on the phone for no reason other than a missing
feature. One deliberate download would turn a near-empty grid into a nearly complete one immediately.

## Non-goals

- Not a replacement for capturing live frames. A frame the app captured itself is fresher and sharper than
  a canned one, so the local capture path stays exactly as it is.
- No automatic or background download. The existing rule stands: the app fetches from the network only when
  the user explicitly asks.
- No previews for radio/audio channels - the publisher deliberately produces tiles only for video.
- Not a new picture cache. The pictures land in the store the grid already reads from.
- No change to the catalog ZIP contract, and no change on the publisher side.

## Constraints

- **The sheet is far too large to keep in memory.** It is one very large image; on Windows there is no way
  to read a single tile out of it without unpacking the whole thing, which costs hundreds of megabytes. It
  must therefore be unpacked **once**, cut into per-channel pictures, written to the picture store, and
  released immediately - never held while the app runs. This is a hard requirement, not a preference.
- **The picture format is not guaranteed to be readable on every supported Windows version.** On machines
  that lack the codec, the feature must quietly behave as "not installed" and leave the app exactly as it
  is today - no crash, no alarming message.
- The tile geometry is a contract shared with the publisher. It is read from the published description, not
  guessed, and a picture index that does not fit the sheet yields no picture rather than an error.
- The companion index file is keyed by stream URL, which is the same key the merge and the picture store
  already use.
- The published asset is version-pinned. A future incompatible rebuild is a different asset; nothing
  silently upgrades itself.
- User-owned rows and the explicit-refresh contract are untouched: this adds pictures, never channels.

## Acceptance criteria

1. From a clean state, after the user asks for the pictures, the grid shows real broadcast frames for the
   large majority of video channels without connecting to any stream.
2. Audio channels and video channels absent from the sheet are visually unchanged - they keep the favicon.
3. A picture the app captured from a live stream is never replaced by a canned one; the canned picture only
   fills a slot that has none.
4. "Refresh previews" still captures a fresh live frame afterwards, and that fresher frame wins.
5. Memory returns to its normal level once the pictures have been extracted; no lasting growth, and the app
   stays responsive during the work.
6. On a machine that cannot read the picture format, the app behaves exactly as it does today, and says so
   plainly if the user asked for the pictures.
7. The pictures survive a restart and an explicit catalog update, like the ones the app captures itself.
8. Nothing is downloaded unless the user asks for it.

## Risks

- The one-off extraction is memory-heavy; done carelessly it would stutter the interface or defeat the
  memory work just completed.
- The picture format may be unreadable on older Windows, which would make the feature silently unavailable
  for some users - acceptable only if the fallback is invisible.
- Canned pictures could mislead: a frame captured weeks ago may not represent a channel that has since
  changed or died. They are a starting point, and a real capture must always be able to override them.
- The publisher may rebuild the sheet with a different layout; the app must not assume today's numbers.

## Settled questions

1. **How is the download offered? - RESOLVED (owner, 2026-07-26): option (c).** An offer appears after a
   catalog update, is declinable, and is re-offered on every later catalog update until it is accepted.
   Nothing downloads without that acceptance. This mirrors the mobile app, whose behaviour was already
   tuned against owner feedback: only an *accepted* offer latches, so declining never silences the feature
   permanently - that re-offer is also the user's way back if they change their mind.

   Rejected: (a) riding the catalog update automatically, which would add ~11 MB to every refresh for users
   who never open grid view; (b) an always-present explicit action as the *only* entry point, which would
   leave the feature undiscovered.

## Open questions

2. Should the user be able to delete the downloaded pictures to reclaim disk space, beside the existing
   "delete downloaded catalog" action? Leaning yes for symmetry, but deliberately **deferred** - it is not
   required by any acceptance criterion above and would widen this ticket's UI surface. Revisit once the
   feature ships and the real disk footprint is known.
