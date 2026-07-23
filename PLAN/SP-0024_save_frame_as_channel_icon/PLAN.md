# SP-0024 Tactical Plan: Save current frame as channel icon

**Status:** In Progress
Strategic ticket: `PLAN/SP-0024_save_frame_as_channel_icon.md` (Approved).

## Evidence summary

Every building block already exists; only the on-video toast is new.

- Snapshot of the **currently displayed** frame: `PlayerWindow.CaptureThumbnail()` →
  `_mediaPlayer.TakeSnapshot(0, path, 480, 0)` → async `MediaPlayer_SnapshotTaken` →
  `_onThumbnail?.Invoke(_channel.Url, frame)` (`PlayerWindow.xaml.cs:177-205`).
- Adopt-as-grid-icon: `_onThumbnail` = `GridPreviewCoordinator.IngestFrame(url, frame)`
  (`MainWindow.xaml.cs:828`) → repaints tile + persists per-URL JPEG in `grid-previews`
  (`GridPreviewCoordinator.cs:198-216`, `PreviewFrameStore.cs`). Newest-wins, no CSV/merge touch.
- Bottom panel + auto-hide: `ControlPanel` right `StackPanel` (`PlayerWindow.xaml:21-38`);
  `ShowControls`/`ControlsHideTimer_Tick` (`PlayerWindow.xaml.cs:646-663`). A new button inside
  the panel auto-participates in fullscreen hide/reveal.
- Glyph convention: `PlayerOverlayGlyphButton` + per-style `GlyphGeometry` (`App.xaml:57-70`).
- Localization: parallel `Key`/`KeyTip` `sys:String` entries in `Localization.en.xaml` /
  `Localization.ru.xaml`; `Content`/`ToolTip` via `{DynamicResource ...}` (`en.xaml:135-140`).
- Channel identity: `_channel.Url` (`PlayerWindow.xaml.cs:29,82`).
- No fade/toast pattern exists — new UI (centered Border, `Opacity` keyframe animation).

## Steps (dependency-ordered; each ends in a static check)

1. **Localization keys.** Add `SaveFrameAsIcon`, `SaveFrameAsIconTip`, `FrameSavedAsIcon` to
   `Localization.en.xaml` and `Localization.ru.xaml` (no emoji).
   *Check:* both dictionaries contain all three keys; build compiles.

2. **Glyph style.** Add `PlayerOverlaySaveIconGlyphButton` (camera figure) to `App.xaml`,
   BasedOn `PlayerOverlayGlyphButton`.
   *Check:* `App.xaml` parses; style key resolvable.

3. **Button + toast XAML.** In `PlayerWindow.xaml`: add the save-frame `Button` to the right
   `StackPanel` (before `MuteButton`) with the new style, `Content`/`ToolTip` dynamic resources,
   `Click="SaveFrameButton_Click"`; add a centered `FrameSavedToast` `Border` (Opacity 0,
   `IsHitTestVisible=False`, `TextBlock` bound to `FrameSavedAsIcon`) as a sibling of `ControlPanel`.
   *Check:* XAML compiles.

4. **Code-behind.** In `PlayerWindow.xaml.cs`: make `CaptureThumbnail()` return `bool`; add
   `_manualSnapshotPending` flag; add `SaveFrameButton_Click` (quiet no-op unless `_reachedLive`,
   set pending, capture, clear pending if snapshot rejected); in `MediaPlayer_SnapshotTaken` show
   toast when a manual save succeeds; add `ShowFrameSavedToast()` (Opacity keyframe fade ~2s).
   *Check:* `dotnet build -c Release` succeeds.

5. **Validate.** `dotnet test -c Release`; run app, open a video stream, click Save icon, observe
   toast + grid tile updates to the chosen frame; confirm no crash before a frame exists.
   *Check:* build+tests green; run-and-observe evidence recorded.

## Evidence

- Step 4: `dotnet build StreamsPlayer.sln -c Release` — expected: succeed | actual: Build
  succeeded, 0 warnings, 0 errors.
- Step 5a: `dotnet test StreamsPlayer.sln -c Release --no-build` — expected: pass | actual:
  Passed 108, Failed 0.
- Step 5b: launched `StreamsPlayer` (Release) — expected: app starts, new App.xaml glyph style
  resolves at runtime | actual: process running, Responding=True (no startup crash).
- Step 5c (pending user's display): open a video/RTSP channel → click **Save icon** → expect the
  camera-glyph button on the bottom panel, a ~2s "Frame saved as channel icon" toast fading over
  the video, and the grid tile adopting the chosen frame (persisting after restart). Clicking
  before a frame renders must do nothing (no crash/blank icon).

## Guardrails

- Reuse the snapshot path; no new capture pipeline; do not touch `_thumbnailCaptured` automatic path.
- No catalog/CSV/`SourceOrigin`/merge changes. No new window/dialog.
- Keep `PlayerWindow.xaml.cs` cohesive; no ad-hoc logging beyond existing `CurrentLog` events.
