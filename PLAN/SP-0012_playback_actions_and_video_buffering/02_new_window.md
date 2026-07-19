# Phase 02 — Independent video window

1. [Done] Add localized command/tooltip resources and add the command to the shared overflow menu.
2. [Done] Launch an independent `PlayerWindow` for video/RTSP without the normal selected/playback side effects; disable it for audio.

Static check: the new command bypasses `PlayChannelAsync` and is guarded by media kind.
