# Phase 02: Capture engine

**Produces:** safe one-frame capture and sequential sweep ownership.

**Status:** Completed

1. Add the official LibVLCSharp wrapper and Windows native runtime packages to the App project.
   - Static check: Release restore resolves both pinned package versions and the native assets are present in App output.
2. Add a 640x360 off-screen decoded-memory surface and a capture primitive that creates a muted short-lived player, copies its first displayed frame within 12 seconds, releases the pinned surface, and always disposes player/media.
   - Static check: every success, failure, timeout, and cancellation path reaches the same teardown block; callbacks and pinned memory cannot outlive the player.
3. Add a coordinator with one worker, URL pending-set deduplication, 60-second refresh cadence, forced requests, and a single global enable flag.
   - Static check: only the worker invokes capture; stop cancels the session and clears pending work; failed capture never clears the cached image.
