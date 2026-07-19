# Research: live grid preview thumbnails

**Date:** 2026-07-19

## Repository evidence

- `MainWindow.xaml` already renders virtualized outer rows of responsive compact cards. This can remain the list presentation while a second tile template reuses the same row virtualization.
- `MainWindow.xaml.cs` rebuilds presentation rows after load, filtering, pinning, and playback outcomes. It currently creates immutable `ChannelRow` records, so URL-targeted frame updates need an observable row property.
- `CatalogState` is already persisted by `StreamCatalogStore`; a platform-neutral List/Grid preference can be added without coupling Core to WPF.
- `FaviconTileLoader` already supplies the required fallback image. There is no frame cache, viewport observer, capture queue, or media dependency.
- The current `MediaElement` playback backend has opened/failed events but no dependable first-rendered-frame snapshot API.

## External contract evidence

- The supplied feature brief limits capture to HTTP(S) video, specifies sequential visible-only work, 12-second timeout, 64-entry caches, 60-second freshness, 640x360 JPEG frames, and stale-frame precedence.
- VideoLAN documents that native LibVLC must be supplied separately from its .NET wrapper: https://docs.videolan.me/libvlcsharp/docs/getting_started.html
- VideoLAN's `MediaPlayer` API exposes fixed decoded-video format and memory-buffer callbacks for cases where window embedding does not fit, including `RV32`: https://docs.videolan.me/libvlcsharp/api/LibVLCSharp.Shared.MediaPlayer.html
- The official NuGet packages current during research are LibVLCSharp 3.10.0 and VideoLAN.LibVLC.Windows 3.0.23.1.

## Settled decisions

- Keep foreground playback unchanged. Preview capture is a cohesive App-only service around a fixed 640x360 decoded-memory surface and short-lived `MediaPlayer` instances.
- Treat LibVLC's first completed display callback as frame readiness and copy that frame within the overall 12-second bound. This avoids dependence on a desktop-visible HWND and uses LibVLC's supported capture surface rather than WPF rendering copies.
- Use the existing virtualized row model to compute realized visible channels after load, mode changes, filter changes, resize, and scroll.
- Persist the view preference in `CatalogState`; preview JPEGs live in a dedicated directory beside other local StreamPlayer data.
- Restored disk entries are immediately displayable but never fresh. Capture failure changes reachability only and never clears an existing frame.

## Validation needs

- Deterministic tests cover persisted view mode and cache-independent eligibility/hash rules where practical.
- Build checks prove package/native asset wiring.
- GUI observation must prove mode switching, responsive tiles, scroll behavior, and one real captured frame.
