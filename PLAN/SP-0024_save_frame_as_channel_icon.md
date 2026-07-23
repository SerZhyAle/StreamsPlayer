# SP-0024: Save current frame as channel icon

**Status:** Implemented

## Goal

Give the user a one-click control on the player's bottom panel that captures the
frame currently on screen and adopts it as that channel's grid icon (thumbnail),
so the user — not an automatic heuristic — chooses the picture that represents the
channel.

## Why

Today a channel's grid thumbnail is filled automatically: the headless grabber
takes the first reachable frame, and opening a channel adopts the first live
frame. The user has no way to say "this exact picture is the one I want." Auto-
captured frames are often a black intro, a loading slate, or an ad. When someone
is already watching and sees a good representative frame, the cheapest possible
way to get a good icon is to let them save that frame on the spot.

## Non-goals

- No new capture pipeline: reuse the existing snapshot → adopt-as-thumbnail path.
- No editing, cropping, or filtering of the captured frame.
- No change to the automatic first-frame / headless / hover capture behaviour,
  nor to the "update thumbnails automatically" setting semantics.
- No change to the catalog CSV contract or the MANUAL/IMPORTED merge protection.
- No new window or dialog.

## Constraints

- The control lives only on the player's bottom control panel; it is not added to
  the grid tile menu in this ticket.
- The player window is video/RTSP only, so the control targets a video frame; it
  must do nothing harmful before a frame exists (early connection, black screen).
- The saved frame is persisted for the channel by its URL and shown on the grid
  and on next launch, using the existing thumbnail store and its size budget.
- The control follows the existing bottom-panel glyph and layout convention and
  auto-hides/reveals with the rest of the panel in fullscreen.
- Its label/tooltip is localized in English and Russian; no emoji is introduced.
- Thumbnails are independent of a channel's `SourceOrigin`; saving a frame must
  not alter catalog/manual/imported classification or the refresh merge.

## Acceptance criteria

1. The player's bottom panel shows a clearly identifiable "save current frame as
   icon" control while a stream is open.
2. Activating it captures the frame currently displayed (not only the first frame)
   and that picture becomes the channel's grid icon immediately and after restart.
3. The control does not misbehave when no frame is available yet; it either stays
   unavailable or fails quietly without a crash or a broken/blank icon.
4. On save, a brief message (~2 s) fades in over the video confirming the frame
   was saved as the channel icon, and it is legible in fullscreen.
5. Automatic capture behaviour, the thumbnail setting, and catalog/merge
   protection are all unchanged by this feature.
6. Build/tests pass and a run-and-observe check confirms the grid icon updates to
   the chosen frame from a live player session.

## Decisions

1. **Stickiness — newest wins.** A manually-saved frame behaves like any other
   capture; it is not protected from later automatic thumbnail updates. Re-opening
   the channel or a later automatic capture may replace it, and saving again simply
   overwrites the current icon. (Chosen for simplicity; if users later ask to keep
   a chosen icon, protection is a follow-up ticket.)
2. **Confirmation — brief on-video toast.** Saving shows a short (~2 s) message
   fading in over the video, legible in fullscreen, then clearing on its own.

## Risks

- Because saves are newest-wins, a user-chosen icon can be overwritten by the next
  automatic capture on re-open. This is accepted for now; the toast makes the save
  itself unambiguous.
- Snapshotting the live player must not stall or disturb ongoing playback.
