# Research — SP-0012

## Evidence

- `MainWindow.PlayChannelAsync` already calls `StopAudio` before starting any other audio stream. Video/RTSP opens a new `PlayerWindow` and does not stop audio or another player window.
- List cards only expose Pin and Play; Grid tiles expose the shared `OverflowButton_Click` menu.
- `PlayerWindow` uses WPF `MediaElement`, whose opened/failed events cannot configure network caching or report buffering percentage.
- The App already includes LibVLCSharp 3.10.0 and VideoLAN.LibVLC.Windows 3.0.23.1 for previews. The preview service proves `Media` options `:network-caching` and `:live-caching` work with this runtime.
- VideoLAN documents `MediaPlayer.NetworkCaching`, the `Buffering`, `EncounteredError`, and `TimeChanged` events, plus the WPF `VideoView` approach. It explicitly recommends native video output rather than decoded-frame copying for performance: [MediaPlayer API](https://docs.videolan.me/libvlcsharp/api/LibVLCSharp.Shared.MediaPlayer.html), [WPF guidance](https://docs.videolan.me/libvlcsharp/docs/getting_started.html).

## Settled decisions

- Persist no playback-session state; audio-card presentation is transient App state.
- Keep normal Play as the single active audio path; make the independent player command explicit and video/RTSP-only.
- Replace only foreground video/RTSP rendering with LibVLC WPF VideoView, using a 10-second cache and buffering events. Audio remains WPF `MediaElement`.

## Check

Expected: current video surface can configure network/live buffering and progress.  
Actual: `PlayerWindow` contains only `MediaElement`; no cache or buffering event is present.
