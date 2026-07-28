# SP-0038: Save the current frame to a file

**Status:** Verified

## Goal

Turn the player's frame control into a real screenshot feature: pressing it writes
the frame currently on screen to a picture file the user can find, open and share,
in a folder the user chooses once in Settings - while keeping the SP-0024 behaviour
of adopting that frame as the channel's icon.

## Why

SP-0024 gave the button one effect only: the frame became the channel's grid icon,
stored inside the application's private state folder. Nothing reached the user's own
file system, so "save" did not mean what the word promises - the picture could not be
opened, sent, or kept. The owner's intent for the control is a camera: capture what is
on screen, put it somewhere the user owns, and say so.

## Non-goals

- No new capture pipeline: the existing snapshot path stays the only source of frames.
- No editing, cropping, annotation, or format choice beyond the fixed JPG output.
- No change to automatic grid previews, the headless grabber, or the "update stream
  previews" setting.
- No burst/interval capture, no video recording.
- No change to the catalog CSV contract or the MANUAL/IMPORTED merge protection.

## Constraints

- The control stays on the player's bottom panel and keeps working in fullscreen.
- A capture must never disturb playback and must fail quietly before a frame exists.
- The chosen folder is user data: it persists in the existing local state file and an
  unreadable or missing value degrades to the default rather than blocking a save.
- Writing happens off the UI thread; a failed write (missing folder, no permission,
  full disk) is reported to the user, never thrown.
- Every user-visible string ships in all thirteen interface languages.

## Acceptance criteria

1. Pressing the control while a stream is playing writes one picture file of the frame
   on screen into the frames folder, and the same frame becomes the channel's icon.
2. The file is JPG at 75% quality, at the stream's own resolution, named after the
   channel and the capture time.
3. Settings offers a "frames folder" the user can pick; when it is unset the file goes
   to the Windows Downloads folder.
4. A brief on-video message reports the result - the saved file name on success, a
   failure message otherwise - and stays legible in fullscreen.
5. The control is a camera glyph with no caption, and names itself through its tooltip
   like the other player buttons.
6. Build and tests pass, and a run-and-observe check shows a real file on disk from a
   live session plus the updated channel icon.

## Decisions

1. **Both effects, one press.** The frame is written to a file *and* adopted as the
   channel icon (SP-0024 is kept, not replaced).
2. **Stream resolution.** The manual capture is taken at the stream's own resolution;
   the icon is derived from that same frame by downscaling, so one capture serves both.
3. **File name `<channel>_<yyyyMMdd-HHmmss>.jpg`**, characters illegal in a Windows file
   name replaced, in the frames folder.
4. **Default folder: Downloads.** An unset setting means the Windows Downloads known
   folder, resolved at save time rather than being written into the state on first run.
5. **Setting lives on the Playback tab** of Settings, beside the other player options.

## Risks

- A folder the user picked can disappear or become read-only between sessions; the save
  path must surface that as a message instead of an unhandled exception.
- Full-resolution JPGs are larger than the previous 480-wide icon captures; this is the
  point of the feature, but the frames folder grows with every press.

## Verification - agent-driven UIA run (2026-07-28)

Two live sessions driven through Windows UI Automation against the real local state; evidence under
`temp/scratch/`.

- expected: a caption-less camera button naming itself through its tooltip | actual: automation name
  `Зберегти кадр`, help text `Зберегти поточний кадр у файл і як значок каналу`; the glyph-only button is
  visible on the panel in `temp/scratch/frame-toast-0.png`.
- expected: one JPG at stream resolution lands in the frames folder | actual: with the setting unset,
  `Downloads\demo.unified-streaming.com_20260728-151606.jpg`, 222 KB, 1680x750, JPEG - matching the
  `2200k 1680x750 avc1` overlay the stream itself prints.
- expected: the file is named after the channel when launched from the catalog, into the configured
  folder | actual: `temp\scratch\frames\Tears of Steel (Blender download server, 720p mov)_20260728-151928.jpg`,
  139 KB, 1280x534; log line `FRAME SAVE | ok=true | size=1280x534 | path=..`.
- expected: an on-video message names the saved file | actual: `Кадр збережено:
  demo.unified-streaming.com_20260728-151606.jpg` (`temp/scratch/frame-toast-0.png`).
- expected: the channel icon still updates from the same press (SP-0024 kept) | actual: the preview store
  wrote `8972b20b...jpg` (18 KB) at 15:19:28, the second of the capture.
- expected: Settings > Playback owns the folder | actual: `FrameFolderBox` showed the configured path, the
  two buttons read `Вибрати теку..` / `Використовувати «Завантаження»`, and reset returned the box to
  `C:\Users\serzh\Downloads` (`temp/scratch/settings-frame-folder.png`).
- `dotnet build -c Release` + `dotnet test -c Release`: expected green | actual: 0 warnings, 0 errors;
  `Passed! Failed: 0, Passed: 328`.

A first run wrote a 0-byte file: the JPEG encoder was built on the UI thread and refused to save from the
worker, and the exception died in an unobserved task. The encoder is now created inside the worker and the
frame is frozen (or copied into a freezable) before it leaves the UI thread.
