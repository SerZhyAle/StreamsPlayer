# Phase 5 - Verify

Consumes Phase 4. Run-and-observe; a green build proves nothing here (`docs/agent/VALIDATION.md` level 7).

Run in the sandbox (`Enter-SpSandbox`, see `memory/MEMORY.md`) so the owner's real catalog, pins, and
history are never touched.

## Checks - record `expected: ... | actual: ...` for each

1. **Offer appears only after a catalog update, never on its own.** Launch, open grid view, wait.
   - expected: no offer, no network request for either asset. Then press "Update catalog" -> the offer bar
     appears. (**AC 8**)
2. **Decline is not permanent.** Press "Not now" -> bar hides. Press "Update catalog" again.
   - expected: the offer returns.
3. **Accept seeds the grid without touching any stream.** Accept; watch `Current.log`.
   - expected: real broadcast frames appear for the large majority of video tiles, and the log contains no
     `PREVIEW FAIL` / capture activity for them. (**AC 1**)
4. **Coverage matches the measurement.** Count `grid-previews/*.jpg` before and after.
   - expected: roughly +1876 files (90.5% of the 2072 video channels), no file for any audio channel.
     (**AC 2**)
5. **Memory returns (AC 5).** Sample the process working set before the import, at its peak, and 30 s after
   it finishes.
   - expected: a transient several-hundred-MB peak, then a return to approximately the pre-import level;
     the window stays responsive throughout.
6. **A captured frame is never replaced (AC 3).** Note a channel that already has a captured preview, run
   the import again after clearing the revision marker.
   - expected: that file's timestamp and content are unchanged; the counter reports it as skipped.
7. **A live capture still wins afterwards (AC 4).** On a seeded tile, hover for the dwell or press
   "Refresh previews".
   - expected: the tile updates to a freshly captured 480x270 frame and survives a restart.
8. **Persistence (AC 7).** Restart, then run an explicit catalog update.
   - expected: seeded tiles still shown after both; the offer does not reappear (revision marker matches).
9. **Missing codec (AC 6).** Simulate by pointing the importer at a payload the decoder rejects (or test on
   a machine without the WebP WIC component).
   - expected: a plain "not available" message, no crash, no stack trace, app otherwise unchanged, and the
     revision marker is **not** written so a later attempt is still possible.

## Evidence

Screenshots and the working-set samples under `temp/SP-0031/verify/`. Reference them from the strategic
ticket; do not paste them into it.

## Exit

All nine pass -> set the strategic ticket to `Implemented`, then `Verified` once the owner confirms the
grid on their real catalog. Any fail -> `Partial` with the failing check named.
