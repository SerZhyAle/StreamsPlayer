# Phase 11 - Screenshot pipeline

**Status:** Implemented

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

## Checks

- Full run - expected: 13 PNGs, one size, each verified in its own language | actual: 13 files at
  1366x768, one distinct size, each line of the log naming the control it matched
  (`Interface language`, `Язык интерфейса`, `Sprache der Benutzeroberfläche`, `界面语言`,
  `इंटरफ़ेस की भाषा`, `ইন্টারফেসের ভাষা`, `لغة الواجهة`, `انٹرفیس کی زبان`, ..).
- Owner's state - expected: unchanged | actual: `Real profile restored, catalog-state.json unchanged.`
  SHA256 `85F46432FCBE9B51..` before and after, on every one of the four runs made this session.
- Unknown language - expected: non-zero exit, no PNG, no sandbox | actual: exit 1,
  `'klingon' is not a shipped language. Known listing codes: en-us, ru, uk, ..`, and the aside folder
  was never created - the check runs before anything is moved.
- Right-to-left flip - expected: conditional | actual: fired for `ar` and `ur` only
  (`WS_EX_LAYOUTRTL is set - flipping the capture back.`), silent for the other eleven.
- Canvas reuse - expected: one composition | actual: `tools/store/StoreCanvas.ps1`, dot-sourced by both
  `capture-app.ps1` and the new script; `capture-app.ps1`'s inline copy deleted.
- Superseded files - expected: gone | actual: `auto-capture.ps1`, `app-en-1463x974.png` and
  `app-ru-1463x974.png` deleted; no reference outside `PLAN/DONE/`.

### WS_EX_LAYOUTRTL is set by WPF - the flip is not dead code

I expected this branch never to fire. WPF mirrors in managed layout, and the usual claim is that it
leaves the Win32 extended style alone. Measured: it does **not**. Both the Arabic and the Urdu window
came back with `WS_EX_LAYOUTRTL` set and `PrintWindow` returned them horizontally flipped - Arabic text
reading backwards, the whole image a mirror. Without the conditional flip, two of the thirteen Store
screenshots would have been unusable in a way that no automatic check would notice.

This also confirms the plan's warning that the hazard arrives *with* `PrintWindow`: `CopyFromScreen`
reads composited screen pixels and is unaffected.

### The language verification caught a real failure on its first run

The first attempt refused to write anything: *"The window is not in English: no control is named
'Interface language'."* The cause was the exe selection - the script preferred a
`*win-x64*publish*` folder, and that folder held a build from three days earlier, before the thirteen
languages existed. The old script would have captured that stale window and named the file `app-en.png`
without a word.

Fixed by picking the newest `StreamsPlayer.dll` and **failing when any source file is newer than it**:

```
The newest build (2026-07-27 02:27) is older than Localization.de.xaml (2026-07-27 02:28).
Build first: dotnet build StreamsPlayer.sln -c Release
```

A stale build is worse than no build, and a screenshot pipeline is exactly where that goes unnoticed.

### The verification key is checked for uniqueness first

`LanguagePickerName` is read from each dictionary and the thirteen values are compared before anything
launches. Two languages sharing a value would let a wrong-language window pass verification, so the
script fails and names the collision instead. Currently all thirteen differ.
