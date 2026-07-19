# Phase 03: Grid surface and lifecycle

**Produces:** user-facing grid tiles and visible-only capture triggers.

**Status:** Completed

1. Add accessible List/Grid and Refresh previews controls and a 16:9 grid tile template with center-crop image, title scrim, status dot, pinned badge, play action, and overflow pin action.
   - Static check: audio/RTSP use the same favicon binding and no country fallback exists; all required overlays have bindings or actions.
2. Make rows observable by URL and switch responsive column sizing/template based on persisted view mode.
   - Static check: a frame update raises only its row image/status properties; list cards retain their existing bindings and actions.
3. Observe realized viewport rows after entry, scroll, resize, filtering, and refresh; prewarm those URLs and enqueue only captureable channels.
   - Static check: no code enumerates the entire filtered catalog into the capture queue; explicit refresh uses force while periodic/scroll requests do not.
4. Stop and restart preview ownership across list mode, deactivation/activation, and close.
   - Static check: every inactive transition calls coordinator stop before returning.
