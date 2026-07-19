# SP-0012: Playback actions, parallel player windows, and resilient video buffering

**Status:** BlockNeedUserTest — live video buffering and parallel windows require observation without interrupting the user's active Debug StreamPlayer process.

## Goal

Make the active audio action unambiguous, expose the same stream menu in list and Grid views, allow additional video windows without interrupting current playback, and use a buffered video backend for unreliable live streams.

## Why

Selecting another audio stream already replaces the current one, but its card does not visually become a Stop action. List cards lack the Grid view's command menu. WPF's foreground media backend exposes no controllable network buffer, so live video can freeze rather than trading a small live delay for steadier playback.

## Non-goals

- Mix two audio streams or add a global player-window manager.
- Add automatic catalog refresh, remote playback, recording, or a seek UI.
- Guarantee a public stream's availability or eliminate every provider-side interruption.

## Constraints

- Starting an audio stream stops only the previous audio stream. Opening a normal or additional video/RTSP window never stops current audio or another video window.
- The additional-window command applies only to video and RTSP; it remains unavailable for audio.
- List and Grid use one menu command set. The normal Play/Open command keeps its current semantics.
- Foreground video uses the already-bundled LibVLC runtime, a target 10-second live/network cache, and visible localized buffering status. Initial buffering may remain visible for up to five seconds before video is shown.
- Keep local catalog state, explicit refresh, and Core's platform-neutral boundary unchanged.

## Acceptance criteria

1. The currently playing audio card displays a Stop icon and localized Stop label; any stop path restores Play, and selecting another audio stream leaves only that stream playing.
2. List cards have a three-dot stream menu after Play, with the same commands as Grid tiles.
3. The common stream menu offers **Open in new window** for video/RTSP. It creates an independent player and does not stop existing audio/video playback.
4. Video/RTSP windows show buffering progress during startup/rebuffering and use a target 10-second cache; ready video is allowed a 10–15 second delay from live.
5. Release build/tests pass, and a real video window is observed with buffering/ready state and multiple windows.

## Risks

Live streams differ in latency and may ignore/limit receiver caching, so 10–15 seconds is a target rather than a guaranteed measured delay. LibVLC's WPF view has an overlay/airspace contract that must be kept intact.

## Research

See [research dossier](SP-0012_playback_actions_and_video_buffering/research.md).

## Last Audit

- PASS — `PlayChannelAsync` continues to stop the previous audio before starting a new one; cached card rows now receive transient active-audio state, and every audio stop/failure path clears it.
- PASS — List and Grid both expose the common three-dot command menu; the List button follows Play.
- PASS — **Open in new window** is localized, disabled for audio, and creates a `PlayerWindow` directly rather than routing through normal playback, leaving existing audio/video untouched.
- PASS — foreground `PlayerWindow` now owns an official LibVLC WPF `VideoView`, with 10,000 ms `NetworkCaching`, `network-caching`/`live-caching` media options, RTSP-over-TCP, localized Buffering progress, and event/disposal cleanup.
- PASS — expected: `scripts/check.ps1` succeeds | actual: Release build completed without warnings/errors; 38/38 Core tests passed.
- MANUAL — expected: two live windows play independently and a real stream reports buffering progress before ready video | actual: not run because a user-owned Debug StreamPlayer process is active and was left untouched.
