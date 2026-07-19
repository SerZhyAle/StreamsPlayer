# Phase 03 — Buffered LibVLC foreground player

1. [Done] Add the official WPF LibVLC view dependency compatible with the current LibVLCSharp runtime.
2. [Done] Replace only PlayerWindow's foreground rendering with a window-owned LibVLC player and media configured for 10-second network/live cache and RTSP-over-TCP.
3. [Done] Drive a localized buffering percentage/status from LibVLC events and dispose subscriptions/media/player/runtime on close.

Static check: PlayerWindow no longer uses `MediaElement`; cache options and event cleanup are present.
