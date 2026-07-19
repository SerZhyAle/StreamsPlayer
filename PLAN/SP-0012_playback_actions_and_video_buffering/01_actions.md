# Phase 01 — Active audio and common menu

1. [Done] Add transient active-audio presentation to `ChannelRow` and update it whenever audio starts/stops/fails.
2. [Done] Bind list and Grid play buttons to the active action presentation; add the list overflow button after Play and reuse the existing command handler.

Static check: no playback state enters Core; both templates invoke `OverflowButton_Click`.
