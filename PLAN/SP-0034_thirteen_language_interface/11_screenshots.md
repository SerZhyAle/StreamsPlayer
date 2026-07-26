# Phase 11 - Screenshot pipeline

**Status:** Approved

Decision 9, Decision 11, criterion 10. `tools/store/auto-capture.ps1` is replaced. Its defect is
confirmed and already live: the regex at `:54` matches `(English|Russian)` - the *old* value, not the
requested one - and `[regex]::Replace` is a silent no-op on a miss, so with the owner's saved state on
Ukrainian both existing `app-en-*.png` and `app-ru-*.png` were captured from a Ukrainian window and
written under English and Russian names. Treat the existing assets as compromised.

1. Add `tools/store/capture-store-screenshots.ps1` replacing `auto-capture.ps1`. Take the language set
   from a `-Languages` parameter defaulting to all thirteen, resolved against the Core registry's
   listing codes rather than a list in the script - a fourteenth language must not require editing this
   file.
   Static check: the script contains no per-language literal beyond the registry lookup.

2. Sandbox the owner's state by renaming the real folder aside, not by redirecting `LOCALAPPDATA`: the
   app resolves `%LOCALAPPDATA%\StreamsPlayer` through the known-folder API, so the environment
   variable has no effect (`memory/MEMORY.md:102-105`). Because a capture needs a populated catalog,
   seed the sandbox with a **copy** of the real `catalog-state.json` and atlas rather than an empty
   folder, then delete the sandbox and rename the real folder back in a `finally`. The owner's catalog,
   pins, collections and history are never written to.
   Static check: after a run, the real state file's hash is unchanged.

3. Set the language by writing the sandbox state atomically - temp file plus move, matching
   `StreamCatalogStore` - then **read it back and verify** the stored value equals the requested
   language before launching. A language the registry does not know, or a read-back that disagrees,
   throws. Never continue on a silent no-op.
   Static check: an unknown language argument exits non-zero and writes no PNG.

4. Verify the captured window is actually in the requested language before writing the file. Query the
   window with UI Automation for a control whose name equals that language's value for a chosen
   dictionary key, and throw when it does not match. The window title is not a usable discriminator -
   it is localized, and the current title check (`auto-capture.ps1:73`) is itself hardcoded to two
   locales. Drive the window with the existing UIA helpers under `tmp/uia/`.
   Static check: the verification runs per language and its expected string comes from the dictionary,
   not from the script.

5. Capture with `PrintWindow(PW_RENDERFULLCONTENT)` rather than `CopyFromScreen`, so a tooltip or
   another window cannot land in the shot, and find the window by process and size rather than by its
   localized title. Then handle the mirrored capture: a window carrying `WS_EX_LAYOUTRTL` comes back
   horizontally flipped, so test `GetWindowLong(h, GWL_EXSTYLE) & 0x00400000` and apply
   `RotateNoneFlipX` only when it is set. Do not flip unconditionally. Note this hazard becomes
   reachable *because* of the move to `PrintWindow`; the two changes must land together.
   Static check: the flip is conditional on the extended style bit.

6. Compose onto a fixed Store-valid canvas and name files by listing code, so the file names match the
   listing columns. Reuse the canvas composition already in `tools/store/capture-app.ps1:58-67` rather
   than writing a third variant; the raw window rectangle that `auto-capture.ps1` writes is not a
   Store-valid size.
   Static check: all thirteen outputs report the same pixel size.

7. Delete `tools/store/auto-capture.ps1` and the two compromised captures
   `assets/store/app-{en,ru}-1463x974.png`. Also correct `STORE_PUBLISHING.md:69`, which names
   `screenshot-{en,ru}-1366x768.png` files that no longer exist.
   Static check: `rg 'auto-capture|1463x974' .` returns only historical `PLAN/DONE` references.

Note for phase 13: this script needs a real desktop, a stable screen size and a populated catalog, so
it cannot run in continuous integration. It is the part of the ticket most likely to rot between
releases.
