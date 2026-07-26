# Phase 3 - App importer + bulk store write

Consumes Phases 1-2. The only phase that touches imaging. Carries AC 2-7.

## Steps

1. **`src/StreamsPlayer.App/PreviewFrameStore.cs` - add a bulk path.**
   - `public bool Exists(string url) => File.Exists(ResolvePath(url));` - lets the importer honour AC 3
     without decoding anything.
   - Split the current `SaveAsync` into `SaveAsync(url, frame, ct)` (unchanged behaviour: write + trim) and
     an internal write that skips the trim, plus `public Task TrimOnceAsync(CancellationToken)`.
   - Rationale to state in a comment: `TrimAsync` enumerates the whole directory, so calling it per frame
     across ~1900 writes is O(n^2). The bulk path writes all frames, then trims once.
   - Static check: `dotnet build src/StreamsPlayer.App -c Release`; grep shows the per-frame `SaveAsync`
     still trims (the live capture path must not change).

2. **New `src/StreamsPlayer.App/ChannelPreviewAtlasImporter.cs`.**
   - `internal sealed class ChannelPreviewAtlasImporter(PreviewFrameStore store, CurrentLog log)`.
   - `Task<ChannelPreviewImportResult> ImportAsync(ChannelPreviewAtlasPayload payload, IReadOnlyCollection<string> catalogUrls, IProgress<int>? progress, CancellationToken ct)`
     where the result carries `Seeded`, `Skipped`, `OutOfBounds`, and `CodecUnavailable`.
   - Runs entirely on a worker thread (`Task.Run` at the call site); it must never touch a UI object.
   - Decode once: `BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)`,
     take `Frames[0]`. Scope the decoder and the `MemoryStream` so both are released before returning.
     Comment records the measured cost (+265..491 MB) and that WPF has no region decoder, so this is the
     only workable shape - see `temp/SP-0031/RESEARCH.md` §3.
   - **Codec absence (AC 6):** catch `FileFormatException` and `NotSupportedException` around the decode and
     return `CodecUnavailable` - a normal outcome on a Windows build without the WebP WIC component, not an
     error. Log it once via `CurrentLog`; do not rethrow.
   - Per URL in `payload.Coords`: skip when the URL is not in `catalogUrls`; skip when `store.Exists(url)`
     (**AC 3** - a captured frame is never overwritten); skip when
     `!ChannelPreviewAtlas.IsInBounds(index, frame.PixelWidth, frame.PixelHeight)` counting `OutOfBounds`.
     Otherwise `CroppedBitmap` over the rect, `Freeze()`, JPEG-encode at the store's quality, write via the
     bulk path. Report progress every N tiles.
   - Audio channels are never seeded because the sidecar contains no audio URLs (**AC 2**) - assert nothing,
     the data does it.
   - After the loop: `await store.TrimOnceAsync(ct)`.
   - Static check: `dotnet build StreamsPlayer.sln -c Release` - expected: succeeds, 0 warnings.

3. **Wire the call site in `src/StreamsPlayer.App/MainWindow.Previews.cs`** as
   `private async Task<ChannelPreviewImportResult> DownloadChannelPreviewsAsync()`:
   `ChannelPreviewAtlasService.DownloadAsync` -> `Task.Run(() => importer.ImportAsync(..))` with the catalog
   URL set and a progress hook onto the existing status line. On success only, persist
   `_state = await _store.SaveAsync(_state with { ChannelPreviewAtlasRevision = ChannelPreviewAtlasService.Revision })`.
   Not yet reachable from any control - Phase 4 adds the offer.
   - Static check: build succeeds; grep confirms the only caller is this method.

## Stop conditions

- If the working-set delta after `ImportAsync` returns does not fall back to roughly its pre-import level,
  stop: the sheet is being retained and AC 5 fails. Measure before wiring UI.
- If JPEG-encoding ~1900 tiles takes materially longer than a minute on the owner's machine, stop and
  reconsider seeding only the tiles the grid actually needs rather than the whole sidecar.
