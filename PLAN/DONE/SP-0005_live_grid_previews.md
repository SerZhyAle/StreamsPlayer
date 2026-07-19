# SP-0005: Live grid preview thumbnails

**Status:** Verified

## Goal

Let people switch to a persisted visual grid where visible HTTP(S) video channels progressively show a current frame captured by StreamPlayer, one channel at a time.

## Why

Favicons identify channels but do not show what is live now. A sequential, visible-only preview sweep gives the catalog a useful channel-wall view without opening many decoders or requiring a thumbnail service.

## Non-goals

- Capture audio or RTSP streams.
- Download or refresh the catalog automatically.
- Replace the existing foreground playback backend.
- Capture channels outside the currently realized viewport.
- Guarantee that every public stream is decodable or reachable.

## Constraints

- A tile prefers any cached frame for its exact URL, then its favicon, then a blank surface; a stale frame never reverts to the favicon during refresh.
- Captures are muted, bounded to 12 seconds, sequential, cancellable, and fully disposed after the video surface is detached.
- Leaving grid mode, deactivating the window, or closing the app stops the timer, queue, and active capture.
- The memory cache is a 64-entry LRU with a 60-second freshness TTL. Disk frames use SHA-256 URL filenames, JPEG quality 75, and a 64-file oldest-first cap.
- Disk I/O does not block the UI thread and cache failures are non-fatal.
- Capture can be disabled through one feature flag without disabling grid mode.
- Preserve explicit catalog refresh and the App-to-Core dependency boundary.

## Acceptance criteria

1. A persisted List/Grid control switches between the existing catalog cards and responsive 16:9 tiles with title, status, pin state, play action, and overflow action.
2. Only visible HTTP(S) `Video` channels are captureable; audio and RTSP tiles permanently use favicons or blank surfaces.
3. Entering grid mode restores visible disk frames before queuing live refresh, scroll queues newly visible tiles, and a 60-second cadence refreshes stale visible tiles.
4. Explicit preview refresh forces visible captureable tiles even when fresh, and catalog refresh also requests a forced preview refresh while grid mode is active.
5. At most one muted decoder session exists at a time; success updates only the matching URL tile and marks it reachable, while failure leaves its existing image and amber status intact.
6. Frames survive restart as 640x360 JPEGs, memory and disk remain capped at 64, and stale/restored frames stay visible until replaced or evicted.
7. Grid exit, window deactivation, and close cancel capture work; timeout, cancellation, and decoder errors do not leak a player or render attachment.
8. Release build and tests pass, and a real HTTP(S) video produces an observed non-favicon tile without freezing the UI.

## Risks

- LibVLC is a large native distribution and decoder failures can occur outside managed exception handling; the global capture flag limits operational blast radius.
- Public live streams are unstable, so final GUI evidence needs a reachable test stream and is separate from deterministic build checks.
- WPF realization and nested row virtualization require observation at multiple scroll positions.

## Research

See [research dossier](SP-0005_live_grid_previews/research.md).

## Last Audit

- PASS — persisted surface. expected: List/Grid survives restart and required tile overlays/actions remain usable | actual: automation observed Grid selected after restart; `tmp/streamplayer-grid-preview.png` shows the 16:9 tile, green status, title scrim, overflow and Play actions.
- PASS — capture scope. expected: only visible HTTP(S) video reaches the capture worker | actual: eligibility rejects audio/RTSP/non-HTTP, realized viewport URLs replace the active visible set, and the worker drops URLs no longer visible.
- PASS — sequential capture. expected: one muted decoder with a 12-second bound and unconditional teardown | actual: only the single coordinator worker calls `CaptureAsync`; the first LibVLC display callback copies one frame and `finally` stops/disposes the player and frees pinned memory.
- PASS — cache contract. expected: stale frames remain visible, 64-entry LRU/disk caps, SHA-256 JPEG persistence | actual: eviction clears the row reference; restored entries are stale; runtime evidence is a 640x360, 36,735-byte JPEG named by 64 lowercase hex characters.
- PASS — real frame. expected: a current broadcast image replaces the favicon without freezing or crashing | actual: 1+1 International captured a live studio frame, repainted green, refreshed the same JPEG, and the process stayed responsive; evidence: `tmp/streamplayer-grid-preview.png`.
- PASS — lifecycle. expected: list mode, deactivation and close stop capture | actual: all three call serialized coordinator teardown; two automated launches closed with no remaining StreamPlayer process.
- PASS — documentation. expected: user and privacy text reflect periodic Grid network access and local frames | actual: EN/RU/UK READMEs and localized privacy copy describe both.
- PASS — release check. expected: `./scripts/check.ps1` exits zero | actual: Release restore/build succeeded with 0 warnings and 0 errors; all 27 tests passed.
- DIAGNOSTIC — expected: scoped `dotnet format --verify-no-changes` reports the documented baseline only | actual: ENDOFLINE diagnostics match the repository's pre-existing LF/CRLF baseline; no formatting gate was claimed.
