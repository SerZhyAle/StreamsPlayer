---
name: references
description: External references, standards, and cross-repository dependencies
 type: reference
---

# References

## References

- Toolbar glyph icons: `App.xaml`'s shared `GlyphButton` template applies **both**
  `Fill` and `Stroke` = Foreground to the swapped `GlyphGeometry`, so any closed/near-closed
  path renders as a solid silhouette (fine for a gear or eye, wrong for an outline shape like
  a clock face whose hands would vanish). For an outline icon, give the style its own
  `ContentTemplate` with `Fill="Transparent"` instead of only swapping `GlyphGeometry` -
  see `HistoryGlyphButton` (SP-0019). Confirmed 2026-07-22.

- **Playback run-and-observe needs no UIA at all: launch with `--url` and read the log.** The app takes
  `--url <stream>` (`StreamLaunchRequest.Parse`) and opens the player on it directly, so a playback
  behaviour can be proved by the events in a fresh `Current.log` instead of by driving the tree and
  taking screenshots. Three traps, each one wasted run (SP-0070, 2026-08-08): (1) `dotnet` is **not** on
  the PATH of the agent's Bash tool - launch from PowerShell; a launch that silently did nothing looks
  exactly like an app that started and closed, so confirm by the `Application startup.` line, never by
  the absence of an error. (2) `dotnet run` in a backgrounded task **dies when that task ends** - the
  first attempt was killed at 89 s, mid-freeze, seconds before the event it was there to catch. Use
  `Start-Process` on the built exe so the process outlives the command. (3) A Release build fails with
  `MSB3027 file locked by StreamsPlayer` whenever the owner has the app running from `bin/Release`;
  `-c Debug` still proves compilation, and the Release gate can wait until the process is closed.

- **Capping an adaptive stream's rendition in libvlc 3 costs a re-open, and the option is per media.**
  `:adaptive-maxwidth` / `:adaptive-maxheight` (both present in `VideoLAN.LibVLC.Windows 3.0.23.1`'s
  `libadaptive_plugin.dll`, alongside `adaptive-logic`, `adaptive-bw`, `adaptive-livedelay`,
  `adaptive-lowlatency`, `adaptive-maxbuffer`, `adaptive-use-access`) are read when the media is opened,
  so there is no runtime rung switch on this libvlc generation - changing the cap means playing a new
  `Media`. Set **both** dimensions: the representation selector excludes a rendition whose width *or*
  height exceeds the limit. FlyleafLib's equivalent is `Config.Video.MaxVerticalResolutionCustom`
  (0 = no limit), also read at open. Confirmed working 2026-08-08 (SP-0071): the same channel delivered a
  steady 730-900 kbps at `disp_fps` 24-27 under a 796k/640x360 cap where the uncapped 2096k rung collapsed
  to `disp_fps=0` within seconds. **Confirmed directly 2026-08-08 (SP-0077)**, at the resolution rather
  than through a byte counter: under an 848x480 cap the engine climbed and stopped exactly on that rung
  (707-1084 kbps), where the same source uncapped reached 1920x1080 at 7 163-11 155 kbps.
  **Corrected 2026-08-08 (SP-0076):** this entry used to end "cap only to a resolution the stream really
  offers - the selector has nothing to fall back to" when every rendition exceeds the limit. Measured
  against a five-rung playlist with a deliberate 200x100 cap, libvlc 3.0.23 **played anyway**: `PLAYBACK
  LIVE ttff_ms=357`, no error. So an over-tight cap is not automatically a black screen on this build. Do
  not read that as a licence to invent ceilings - the fallback is not a documented guarantee and says
  nothing about FlyleafLib - but a design may now treat "the cap fits nothing" as a quality bug to detect
  and undo rather than as a playback outage to prevent at all costs.

- **To learn which rendition libvlc 3 is actually showing, read the media's track list and take the
  highest video ES id. The two APIs that look right are both wrong on an adaptive stream.** Measured over
  40 samples of a healthy 1080p HLS session (SP-0077, 2026-08-08): `MediaPlayer.VideoTrack`, documented as
  "current video track ID", stays at **-1** for the whole session - there is no selection to read;
  `MediaPlayer.Size(0, ..)` returns **the resolution the video output was first built with** (320x184)
  long after the engine climbed to 1920x1080 at eight times the rate, and it returns `True` while doing
  so, giving the caller no hint the answer is stale. `Media.Tracks` is a **history** of every ES the media
  has opened, ids ascending as the demuxer opens them, so the highest-numbered video track is the one on
  screen - the new entry appears in the same sample the data rate rises. Corollary already recorded in
  SP-0077: that list cannot be used to build a ladder (it omits rungs that never played and reports zero
  bandwidth for all of them), only to say what is playing now. FlyleafLib does expose
  `Player.Video.Width/Height`, undocumented in its package XML and found by reflection.

- **Proving a rendition cap took effect needs `demux_bytes`, a long sample, and an A/B - and the first
  seconds of two sessions are worthless.** `read_bytes`/`in_bitrate` come from the access module the
  adaptive demuxer bypasses, so they are flat noise on HLS (see the counters entry above). `demux_bytes`
  works, but only after libvlc's adaptive logic has climbed: two sessions of the *same* source, one
  uncapped and one capped to the lowest rung, reported **byte-identical** `demux_bytes` for their first
  three samples, which read as "the cap did nothing" and nearly cost a wrong verdict. At +34 s versus
  +48 s the same pair read 25 647 993 bytes / 10 171 kbps uncapped against 1 423 619 / 204 kbps capped -
  18x, unmistakable. Sample past ~30 s, and compare two runs rather than one run against an expectation
  (SP-0076, 2026-08-08).

- **A WPF window can read a local file before its first action without costing anything, if the read
  starts in the constructor.** SP-0076 needed `quality-memory.json` in hand *before* `StartMedia` in
  `Loaded`, and the obvious objection is that this delays the first frame. Start the `Task` in the
  constructor, make `Loaded` `async void`, and `await` it there: by then it has almost always completed,
  and awaiting a completed `Task` resumes **inline** rather than posting a continuation. Measured across
  six sessions: 0.87-1.47 ms between the recall's log line and `PLAYBACK OPEN`, and `ttff_ms` 343 with no
  record against 344 with one. The constructor-to-`Loaded` gap - window creation, XAML load, measure and
  arrange - is what pays for the read (2026-08-08).

- **A relaunch that races the dying process can produce a session with no log at all.** Killing
  StreamsPlayer and starting it again in the same command left the new instance playing for two minutes
  having written **zero** lines: `DiagnosticLogFiles` retires `Current.log` at launch, the rename failed
  silently while the old process still held the handle, and the session then logged to nothing. This is
  the SP-0070 trap's twin - "started and closed" and "started and logged nothing" look identical - and it
  cost one run of SP-0076's observation. When scripting run-and-observe, wait for the process to actually
  exit before relaunching.
  **Fixed in SP-0085 (2026-08-08), and the mechanism is worth keeping:** a live session holds its log
  with `FileAccess.Write` + `FileShare.Read`, which denies *both* halves of the launch - `File.Move`
  needs delete-sharing to retire it, and a second `FileMode.Create` on the same name is a sharing
  violation the constructor's blanket `catch` then swallows. `DiagnosticLogFiles.Rotate` now returns
  `LogRotationOutcome`, and a blocked launch writes to `ReserveSessionPath(..)` - its own
  `Session-<start>.log` - opening with `LOG ROTATION FAILED`. So a raced relaunch is diagnosable again,
  but note where to look: **that run's log is not `Current.log`**, and the newest `Session-*` file may be
  a session still running rather than a retired one. Retention overshoots by one file for that launch and
  the next launch prunes it back.

- GUI run-and-observe without a human: drive the app from PowerShell with
  `Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes` and screenshot with
  `System.Drawing` `CopyFromScreen`. Three traps found 2026-07-24 (SP-0030): (1) `PlayerWindow`,
  `SettingsWindow`, and `MessageBox` are **descendants** of the main window in the UIA tree, not
  root children - `FindFirst(TreeScope.Children, ...)` on the desktop only ever returns
  `Трансляции`/`STREAMS Player`; (2) Settings tabs exposed no usable `Name` (headers are
  StackPanels), so they had to be selected by index via `SelectionItemPattern` - **fixed in SP-0064**,
  they are addressable by label now; (3) `ShowDialog` disables the other
  app windows, so a topmost `PlayerWindow` left over from launch resume cannot be closed and will
  sit on top of every screenshot - close it *before* opening Settings, and `SetForegroundWindow`
  is unreliable against foreground lock.
  Three more traps, each one debugging round, found 2026-08-06 (SP-0045): (1) call
  `user32!SetProcessDPIAware()` **first** - without it `GetWindowRect` returns virtualized
  coordinates while `CopyFromScreen` uses physical ones, and the capture is a correctly sized
  crop of the wrong part of the screen, which looks like a wrong window rather than a DPI bug;
  (2) a `DllImport` of `GetWindowText` needs `CharSet=CharSet.Unicode` or the StringBuilder
  marshals ANSI against the W entry point and every title comes back as its first byte
  (`'Трансляции'` reads as `'"@0=A;OF88'`), so a title filter silently matches nothing;
  (3) `PlayerWindow`'s control panel is woken by `VideoSurface_MouseDown`, **not** by a mouse
  move - a synthetic `SetCursorPos`/`mouse_event` move leaves it auto-hidden and every capture
  is bare video. Send a single left click (only a *double* click toggles fullscreen), then
  capture within the 10 s `ControlsHideTimeout`. Shell state does not survive between tool
  calls, so the whole `Add-Type` + find + click + capture sequence must be one invocation.
  Two more, each one debugging round, found 2026-08-08 (SP-0058), and both look like "the control is
  missing" rather than what they are: (4) **`Set-SpForeground` blinds the automation tree.** Its
  foreground-lock bypass taps ALT, which leaves WPF in menu/access-key mode, and while the window is in
  that mode its *content* peers are not exposed at all - the tree collapses to the non-client chrome and
  stays collapsed. Measured (`temp/SP-0058/probe4.ps1`): 13 buttons at rest, 13 after
  `ShowWindow`+`SetForegroundWindow`+`BringWindowToTop`+`SetWindowPos(topmost)`, **6 after a bare ALT
  tap**, 13 again after Escape. So the ALT tap is the only harmful part of `ForceForeground`; pair every
  foreground call with an Escape and poll until the count recovers. (5) **A channel card exposes itself
  as one `DataItem` and nothing inside it.** WPF caches an item container's automation peer, and a
  container realized by virtualization starts out with no children, so a card's own buttons and texts are
  unreachable by name however long you poll - and neither clicking the card, filtering the list, nor
  `RevealChannelAsync`'s `ScrollIntoView` brought them back. The card's *rectangle* is reported
  correctly, so the overflow glyph has to be clicked by geometry (32 px wide, against the card's right
  padding, on the title line) with the list narrowed to a single row so the click cannot land on the
  wrong channel. Note the consequence for the header's own menus: a `BuildEntry` item's UIA name is its
  **tooltip** key, not its header, so the operations entries are found by their description
  ("Add a channel someone sent you as text.."), while the overflow entries carry their header text.
  A shutdown-path variant, 2026-08-08 (SP-0065): to observe a *crash on exit* you need the real cursor over
  a real tile at the moment of destruction, so park it with `SetCursorPos` (**two** calls - WPF raises
  `MouseEnter` on a move delta, not on a position), then `PostMessage(hwnd, WM_CLOSE)` and read
  `Process.ExitCode` plus the tail of `Current.log`. The pass condition is the `Application shutdown.` line,
  because that is what `App.OnExit` writes and what an unhandled exception skips. A clean exit alone proves
  little when the bug is intermittent - temporarily log from the guarded handler so the run also shows the
  late event *arriving*, then remove the instrumentation and re-run. Harness kept at
  `temp/SP-0065/observe.ps1`. Note it persists whatever view mode it switches to, so restore the owner's
  preference afterwards.
  The cheapest way past traps (1) and (4), found 2026-08-08 while resizing Settings: **stop using UIA to
  find the dialog and stop trying to own the foreground.** `EnumWindows` filtered by the process id
  returns every top-level window including the modal (diff the list taken before the click against the
  one after), `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)` captures a window that is *behind* the
  editor so no foreground call is needed at all, and `MoveWindow` resizes it - a whole run with no
  synthetic mouse and no DPI arithmetic. Open the dialog with `InvokePattern`: it throws
  `"Unrecognized error"` because the modal takes the message loop, but the window **does** open, so
  catch and continue. Everything else that "should" work does not: `SetForegroundWindow` from a
  background host is refused, a posted `WM_KEYDOWN` never reaches the focused element, and a coordinate
  click lands on the editor. Harness kept at `artifacts/verify-settings-window.ps1`.

- `StreamCatalogStore.SaveAsync` calls `RemoveUnreferencedAtlases` on **every** save, deleting any
  `favicon-atlas-*.png` that the just-saved state does not name. Consequence when testing against
  the real `%LOCALAPPDATA%\StreamsPlayer` state: a backup copy of `catalog-state.json` restored
  after a refresh points at an atlas file that no longer exists (icons go blank; no crash - the
  loader `File.Exists`-guards). Repair is one explicit **Import channels from the internet** (named
  **Update catalog** before SP-0059), which merges by URL and
  keeps ids, pins, outcome marks, and history links. Confirmed 2026-07-24.

- Never edit a repo file from `powershell.exe` (Windows PowerShell 5.1): `Get-Content -Raw` reads a
  UTF-8 file **without BOM** as CP1251, so a read-modify-write silently double-encodes every
  non-ASCII character (Cyrillic, and even the `…`/`—` in English strings). It hit
  `Localization.*.xaml` and 19 PLAN tickets on 2026-07-24. Use the `pwsh` 7 tool (UTF-8 by default)
  or the Edit/Write tools. Repair script: decode UTF-8 -> re-encode CP1251 -> decode UTF-8 again
  (`tmp/uia/fix-encoding.ps1`), which is detectable because only mangled text survives that
  round-trip as strictly valid UTF-8.

- Sandbox for GUI runs: the app resolves `%LOCALAPPDATA%\StreamsPlayer` through the Windows
  known-folder API, so setting the `LOCALAPPDATA` environment variable does **not** redirect it.
  Rename the real folder aside instead (`Enter-SpSandbox`/`Exit-SpSandbox` in `tmp/uia/driver.ps1`)
  so destructive checks never touch the owner's catalog, pins, or history. Confirmed 2026-07-24.

- WPF **does** set `WS_EX_LAYOUTRTL` on the HWND when a window's `FlowDirection` is `RightToLeft`.
  The usual claim - that WPF mirrors only in managed layout and leaves the Win32 extended style
  clear - is wrong. Consequence: `PrintWindow` returns Arabic and Urdu windows horizontally
  **flipped** (text reading backwards), while `CopyFromScreen` does not, because it reads composited
  screen pixels. Any capture path that uses `PrintWindow` must test
  `GetWindowLong(h, GWL_EXSTYLE) & 0x00400000` and apply `RotateNoneFlipX` only when set - flipping
  unconditionally mirrors the other eleven languages. Measured on both RTL languages, SP-0034
  phase 11 (2026-07-27).

- `ar-SA` defaults to the **Umm al-Qura (Hijri) calendar**, so setting `CultureInfo.CurrentUICulture`
  to it changes the calendar system, not just the wording: the catalog timestamp rendered
  `1448/02/12 بعد الهجرة` while Windows, the file system and every other application showed
  2026-07-26. Choosing an interface language must not hand the user a date they have to reconcile
  with the rest of their desktop. `LocalizationService.CreateUiCulture` clones the culture with
  `DateTimeFormat.Calendar` set to its Gregorian calendar; month names, digits and ordering still
  follow the language. Any future culture-sensitive formatting must go through that helper, not
  through `CultureInfo.GetCultureInfo` directly. SP-0034 phase 13 (2026-07-27).

- The shipped interface-language list is declared **once**, in `InterfaceLanguages`
  (`src/StreamsPlayer.Core/InterfaceLanguages.cs`): enum member, dictionary code, culture, Store
  listing code, right-to-left flag. PowerShell tooling does not restate it - `tools/InterfaceLanguages.ps1`
  loads the built `StreamsPlayer.Core.dll` **from a byte array** (`Assembly::Load`, never `LoadFrom`,
  which would hold the file open against a later `dotnet build`) and reads `InterfaceLanguages.All`,
  taking the endonyms from the `Language*` keys in `Localization.en.xaml`. So the site generator, the
  Store listing builder and the screenshot pipeline all derive from the application's own declaration,
  and adding a language needs no edit outside Core. Do not add a language table to a script.

- Three PowerShell 7 parse traps that each cost a debugging round in SP-0034 (2026-07-27):
  (1) `"$var: text"` in an interpolated string is a **parse error** - PowerShell reads `$var:` as a
  scope qualifier. Write `"${var}: text"`.
  (2) `$list.Add("{0} {1}" -f $a, $b)` passes **two arguments to `.Add()`**, not one formatted string,
  because inside a method call the comma is an argument separator. Wrap the `-f` expression in its own
  parentheses. Same for `Size = '{0}x{1}' -f $w, $h` inside a hashtable literal.
  (3) Assigning an object to a `[string]`-typed **parameter** variable silently stringifies it -
  `$export = Read-Csv -Path $Export` turned a PSCustomObject into `"@{Path=...}"` and every later
  property access failed with "property cannot be found". Give the result its own name.
  Also: a dot-sourced library must not call `Set-StrictMode` at file scope - it changes the *caller's*
  rules, and here it broke the tool harness's exit-code epilogue.
- The localization **glossary is not gated by anything**. `LocalizationParityTests` checks key sets,
  placeholders, duplicate keys, layout direction against the Core registry, and the loanword
  exception list - it never compares a dictionary against `docs/localization/glossary.md`. So the
  glossary can contradict the shipped strings indefinitely and no build fails. It did: for Ukrainian
  it prescribed `трансляція`/`підбірка`/`превʼю` while the dictionary shipped `потік`/`добірка`/`прев’ю`
  in 28, 8 and 15 places. When the two disagree, check which one the strings actually use before
  "fixing" the dictionary - the glossary is the likelier defect. The Russian row still carries the
  same `stream` defect (2026-07-27).

- Ukrainian renders `stream` as **`потік`, never `трансляція`**, because `Трансляції` is the localized
  product name and the two collide in one window: "Видалити завантажені трансляції" reads as deleting
  the application. Same reasoning applies to Russian (`поток`, not `трансляция`). The product name is
  grammatically plural, so its genitive is `Трансляцій`, and a bare "У Трансляції" reads as a locative
  singular - in prose surfaces use a generic noun plus guillemets, "у застосунку «Трансляції»"
  (2026-07-27).

- **A Partner Center listing import is all-or-nothing per language, not per file.** One invalid cell
  discards that language's column and imports every other language normally - it does not reject the
  upload, and the summary page reports the failures in a list that is easy to read as a whole-file
  rejection. Consequence that costs a cycle if missed: a partial import **mutates the submission**,
  so the export the file was built from is immediately stale and a fresh one must be taken before
  retrying. Measured 2026-07-27 - ten languages dropped on a bad `DesktopScreenshot1`, three imported.

- **The listing import can reference screenshots but never create them.** `DesktopScreenshot*` accepts
  only the asset URL of an image already uploaded to the current submission; a relative filename is
  rejected in a flat CSV upload. So a new language's screenshot goes up through the UI first, and only
  a re-export carries its asset URL back into an importable file. `STORE_PUBLISHING.md` claims *Upload
  folder* accepts a relative path - that remains **unverified**, so build the flat, image-free CSV
  unless someone has actually tested the folder mode (2026-07-27).

- **A `<sys:String>` value in a localization dictionary cannot hold real newlines.** XAML normalizes
  whitespace in element content, so a multi-line UI or mail string collapses into one paragraph with no
  error anywhere. Use literal `&#x0D;&#x0A;` entities and keep the value on one physical line - that also
  keeps the parity gate and the encoding checks simple. Hit while writing SP-0040's mail body, in all
  thirteen files (2026-07-30).

- **`mailto:` cannot carry an attachment on Windows**, so any "send us the file" feature is a prepared
  message plus the archive revealed in Explorer, and attaching stays the user's gesture. Simple MAPI
  would attach automatically but has no client on a webmail-only desktop. Related environment gotcha on
  the owner's machine: the default handler is the *new Outlook*, never configured, so pressing such a
  button opens its account-setup screen instead of a compose window - a `mailto:` flow cannot be observed
  end to end here, which is why SP-0040 proved the link with unit tests instead (2026-07-30).

- **Two traps that make a UIA pass lie about a modal dialog and about the operations menu** (SP-0059,
  2026-08-08). (1) `Set-SpForeground` in `tmp/uia/driver.ps1` raises **the main window**, so calling it
  before injecting a keystroke aimed at a modal puts the owner on top and the key lands there instead:
  Escape appeared to do nothing, the dialog stayed on screen, and the decline that *was* eventually
  recorded came from `Stop-Sp` closing the window later - a green-looking result proving the wrong
  thing. Force the foreground onto the dialog's own `NativeWindowHandle`. Related: the main window's
  UIA `IsEnabled` still reads `True` while it owns an open `ShowDialog`, so it is not a usable "is the
  modal up" probe - enumerate sub-windows instead. (2) `BuildOperationsMenu` passes the **tooltip** key
  as the accessible name for four of its five entries (`BuildEntry(header, tooltip, name, ..)` called
  with the tooltip key twice), so a `MenuItem` search by the visible command name finds nothing. Match
  on the row's `Text` descendant, or on the tooltip. Also: WPF hosts the menu in its own popup HWND, so
  it is reachable from the desktop root and **not** under the application window.

- **GUI evidence via UI Automation: address controls by `AutomationId`, which WPF fills from `x:Name`** -
  language-independent, unlike `AutomationProperties.Name`. Two traps measured on SP-0040: a modal dialog
  is not in the UIA tree the instant `Invoke` returns (poll for one of its children instead of sleeping),
  and `TabControl` content for unselected tabs does not exist at all, so the tab must be selected before
  its controls can be found. `LanguageWindow`'s list was not reachable this way at all; capturing a
  right-to-left window is cheaper by swapping the single root `"language"` token in `catalog-state.json`
  and restoring it afterwards - never by a JSON round trip, which would rewrite 2.9 MB of the owner's
  real catalog (2026-07-30).

- **A WPF control derives its automation name from header *text*, so a composite header yields no name at
  all** - and the control still looks perfectly labelled on screen, which is why the Settings tab strip
  shipped that way for the window's whole life. The `TabItem`s carry
  `AutomationProperties.Name` since SP-0064 and are now selectable by their English label
  (`Language`, `Grid`, `Launch shortcuts`, `Playback`, `Playlists (M3U)`, `About`), which supersedes
  SP-0030's "select by index". `TabAutomationNameTests` gates it, and the pattern it
  uses is reusable for any markup rule: the application's `*.xaml` is already linked into the test project
  as data (SP-0057), XAML is XML, so `XDocument` over `AppSourceFile.LoadAll("*.xaml")` gates markup
  without a project reference. Prove such a gate by mutating the *copy* in the test output and running
  `dotnet test --no-build`, which leaves the source untouched (2026-08-08).

- **The live Store listing is byte-identical to the repo deck again.** A fresh Partner Center export
  taken 2026-07-30 filled **0 cells across all thirteen languages** under both the default run and
  `-ReplaceCopy`, with every language reporting `complete` and the per-language search-term counts
  matching `msix/listing/` exactly. So the ten-language drop measured on 2026-07-27 has been fully
  repaired, and a routine version update needs **no listing import at all** - only the per-submission
  "What's new", which the builder never writes. Re-run the builder before assuming an import is
  needed; an import that changes nothing is pure all-or-nothing-per-language risk (2026-07-30).

- **`SetThreadExecutionState` is per-thread, so `powercfg /requests` lists one entry per *thread* that
  holds a request - never per acquire.** `WakeGuard`'s ref-counted acquires all run on the one WPF UI
  thread and can therefore only ever produce a single `StreamsPlayer.exe` line. Two lines during audio
  is normal: the second is **the Windows audio stack's own request** - reason *"An audio stream is
  currently in use."*, created for any active render stream, appears ~8 s after playback starts, gone
  at stop, unaffected by the setting. **No application API can clear it** (`PowerClearRequest` needs
  the creator's own handle, `SetThreadExecutionState` only adds, no audio-client or Media Foundation
  opt-out is documented); the only lever is to close the stream rather than pause it, which
  `StopAudioPlayback` already does. SP-0051 triaged this and closed Archived - the promise in the
  Settings tip is knowingly broader than what the app controls (2026-08-08). A `DISPLAY x1 + SYSTEM x1` residue
  lingering ~14 s after a player window closes is LibVLC's native teardown settling, not a leak. Reading
  a count as a leak is the easy mistake; the discriminator is to toggle the setting and see which entry
  moves (2026-08-06).

- **Power-state evidence is reachable without a human sitting through an idle timeout.** `powercfg
  /requests` needs elevation, but `Start-Process pwsh -Verb RunAs` gets it with a single UAC click, and
  one elevated *sampler* that polls every 2 s into a log beats one snapshot per UAC prompt - the whole
  acquire/toggle/stop/close/exit matrix then costs one click and a few plain-language instructions to
  the owner. Preferred over shortening `standby-timeout`: the owner's machine has `Sleep after = 0`
  (Never) on AC and DC, and an induced real sleep kills the agent session mid-run (2026-08-06).

- **A push to `origin/main` and even a `v*` tag push can land without starting any workflow.** On
  2026-08-06 the release pass pushed four commits and the tag `v26.0806.2131`; `origin/main` and the
  tag were both visible through the API, Actions reported `enabled: true`, all three workflows read
  `active`, the repository was public and not archived - and neither CI, nor Deploy Pages, nor Release
  produced a run, four minutes after the fact. `gh workflow run release.yml -f tag=v26.0806.2131` and
  `gh workflow run pages.yml` both fired immediately and both went green, so the cause is GitHub's push
  event delivery, not the repo's configuration. Check `gh run list` after every tag push instead of
  assuming latency: `release.yml` carries a `workflow_dispatch` input for exactly this, and a tag that
  silently no-ops looks identical to a slow one until someone looks (2026-08-06).

- **`wingetcreate ... --submit` needs a synced fork, and syncing the fork needs a token scope the work
  itself does not.** The sync fails with "refusing to allow an OAuth App to create or update workflow
  .. without `workflow` scope" whenever upstream `winget-pkgs` has touched `.github/workflows/`, which
  it does constantly. The working path adds no scope at all: branch `SerZhyAle/winget-pkgs` at **its
  own** `master`, PUT the five manifest files through the contents API on that branch, and open the pull
  request from it - GitHub diffs against the merge base, so a fork ~2,900 commits behind still yields a
  five-file, additions-only PR (#413363 proves it). Also: `wingetcreate update` silently drops the
  `ReleaseNotes` fields, so the three locale manifests need them written back by hand or the release
  ships with the previous version's notes (2026-08-06).

- **`VideoLAN.LibVLC.Windows` ships all three native trees into every WPF build, and `-r win-x64` does
  not stop it.** Its `.targets` gates `win-x64`/`win-x86`/`win-arm64` on `$(Platform)`, which is
  `AnyCPU` for a WPF project, so all three are added as `Content` at evaluation time - long before the
  runtime identifier is consulted. Measured on 26.0806.2131: 129.4 MB packed of libvlc, of which
  **84.1 MB (x86 + arm64) can never be loaded** by an x64 package - about 40% of the MSIX and of the
  portable zip. The fix is three properties in `StreamsPlayer.App.csproj` keyed off
  `$(RuntimeIdentifier)`, not off `$(Platform)`, and it must keep an arm64 branch because `build.ps1`
  offers `-Runtime win-arm64`. Verify a packaging change like this by CRC-comparing the surviving tree
  against the previous package rather than by re-testing playback: all 425 `libvlc/win-x64` entries
  matched on size and CRC32, which is what makes the change provably inert (2026-08-06).

- **The single-file EXE from `build.ps1 -Deploy` is not standalone - never hand it to anyone on its
  own.** LibVLCSharp resolves the natives from `libvlc\win-x64\` *beside the executable*, and the
  `VideoLAN.LibVLC.Windows` DLLs arrive as MSBuild `Content`, which `PublishSingleFile` does not embed -
  `IncludeNativeLibrariesForSelfExtract` only covers real native dependencies of the assemblies. Run the
  EXE from a clean folder and it dies at startup in `VideoFrameCaptureService..ctor` with "Failed to load
  required native libraries", listing the paths it wanted. The `-Deploy` flow appears to work only
  because it copies **just the EXE** onto `C:\GD\i` and `C:\GD\tc\SZA\_APP`, which already hold a
  `libvlc\` tree from an older full copy. The distributable artifact is the portable **zip** that
  `.github/workflows/release.yml` builds - a plain self-contained folder publish, no single-file - and
  a build meant for someone else must be produced and verified that way, by extracting it and launching
  the extracted EXE (2026-08-07).
  **Superseded 2026-09-26 (SP-0133):** `-Deploy` no longer builds a single-file EXE. The "older full copy"
  in those roots turned out to be **FastMediaSorter LITE's** `libvlc\` (3.0.21) and `LibVLCSharp.dll`
  (3.9.3) - another product's natives, which StreamsPlayer had been running on. The owner chose a
  `StreamsPlayer\` subfolder per root holding the full release payload; never write StreamsPlayer files
  into those shared roots again.

- **A Partner Center listing import must be CRLF everywhere, and it reports a bare LF as the wrong
  error entirely.** The export is CRLF throughout, inside quoted multi-line cells as much as between
  records. Write one record terminated by a lone `\n` and Partner Center's reader does not close that
  record: the **last** column's quoted cell stays open and swallows the following row, so the rejection
  reads "Italian / ReleaseNotes / ReleaseNotes is too long (must be 1500 characters or fewer)" - naming
  the last language in the header order, a field that measured 1232 characters, and never the line
  ending. `.NET`'s `(?m)$` matches *before* the `\n`, so `'...\r?$'` strips the CR and leaves the LF -
  which is exactly how a CRLF file grows bare line feeds. Match the terminator explicitly (`\r?\n`),
  emit `\r\n`, normalise newlines inside every cell you write, and assert zero `(?<!\r)\n` before
  writing. Cost one rejected submission (2026-08-07).

- **`--clock-jitter` is a compensation budget, not a leniency switch.** VLC's help: "the maximal input
  jitter that is considered valid and *can be compensated* (in milliseconds)", default 5000. Jitter
  inside the budget is absorbed; jitter beyond it is left uncompensated and the clock reference is
  dropped. So `--clock-jitter=0` compensates **nothing** - every late PCR, however small, breaks the
  clock, and the outputs then fill with silence and skip pictures as early. StreamsPlayer shipped 0 for
  months with a code comment and a `docs/` section both asserting the opposite, because the change *had*
  measured well: worst-case jitter fell 8958 ms → 250 ms. That number fell only because VLC stopped
  accumulating a compensation window - a few large freezes were traded for continuous small clock resets
  (SP-0054, 2026-08-07).

- **libvlc's playback counters mislead in two specific ways; measure rates, not counter differences.**
  (1) `decoded_v` and `displayed` do **not** count the same event: on a stream measured playing smoothly
  at a steady 27-32 fps, `decoded_v` ran at almost exactly **twice** `displayed` (2235 vs 1083) with
  `lost_pics=0`. A "skipped frames" column derived from that difference was written, shipped into a local
  build, and reported 50 % loss on a healthy stream before being removed the same day. Do not reason from
  the gap between two counters whose semantics are unverified - difference the rendered counter over wall
  time and compare that fps to the stream's nominal rate. (2) `read_bytes` and `in_bitrate` come from the
  access module, which the HLS and DASH demuxers bypass, so on any `.m3u8` they are frozen forever -
  `read_bytes=364` and `in_bitrate=0` across a 124 s session, and `in_bitrate=0.0000` while a measured
  1.9 Mbps was arriving. Difference the demux byte counter instead (SP-0054, 2026-08-07).

- **The stream-bank contract is authored outside this repo and is not in it.** The authoritative spec
  set lives at `Contracts/stream-catalog/` (moved there from `Contracts/fastmediasorter/streams-catalog/`
  on 2026-09-22, when the store was reorganized by function rather than by product) - its `README.md` is
  the binding short form carrying the twelve rules, files `01`, `03`, `04` and `09` define the bank, the
  CSV, the favicon atlas and the artwork atlases, and a dated `NN_contract_amendment_*.md` **amends**
  them, so read the highest-numbered amendment first and treat the rest as the rules it edits. Its own words: "Where a consumer's current behaviour differs from a
  rule below, the consumer changes - not the rule." Nothing in `StreamsPlayer` mirrors these files, and
  the former `docs/specifications/` folder (removed 2026-10-09) was unrelated - so when the owner says "the spec set", do not search this
  repository and do not infer the contract from our own code comments. The delivery artifacts themselves
  live on one GitHub release, tag `delivery-so-v1` of `SerZhyAle/FastMediaSorter_mob_v2`; the release
  asset list and `artwork-manifest.json` are the cheapest way to tell what is actually published from
  what a document says is published, and on 2026-08-20 those two disagreed.

- **This repository is no longer only a consumer of contracts - it owns two.** On 2026-09-22 the
  portfolio's desktop UX rules were authored from here: `APP-BEHAVIOUR` (twelve shared moments - the
  no-action exit from a dialog, progress with cancellation, explicit consent, confirming the
  irreversible, a failure offered as actions, localized rendering that cannot throw, layout direction as
  a language property, an accessible name on a glyph-only control, window geometry, first run, and a
  settings window that commits on its own button) and `APP-STYLE` (three themes, one palette table with
  only dynamic references, the role vocabulary, a declared out-of-theme surface, one meaning per glyph).
  Both are **0.9 draft**: every rule was read from this product's code, and the other six Windows
  binaries in the portfolio are declared consumers who have not confirmed anything yet. Two consequences
  that are easy to forget: a change to how a dialog, a long operation, a destructive action or the theme
  behaves here is a **contract change first and code second**, and a defect found in another product's
  interface is an amendment this repository has to write rather than that product's private fix. The
  product's own two deviations - irreversible settings operations bypassing Save, and two sites printing
  a raw exception - were declared as dated exceptions on 2026-09-22 and **closed on 2026-09-23 by
  SP-0109**. The shape that closed them is now the rule for new UI: `SettingsWindow` holds values only
  (an operation goes in the Close-only `ToolsWindow` behind Operations > Tools, a per-channel one in the
  channel's menu), and a failure message is `FailureCauseText` over the exception's *type*, never
  `exception.Message`. Both are CI gates in `DesktopUxConformanceTests`, so breaking either fails the
  build with a message that says where the control belongs. The registry had undercounted rule 6 (two
  leaks named, six real) - a declared exception is a lower bound, so re-search before closing one.

- **A contract may never be a build input from outside this repository.** On 2026-09-22 an earlier,
  unfinished alignment pass deleted the in-repo copy of the live-broadcast contract and pointed the test
  project's `Content Include` at the shared store by absolute path instead. That left the tree unbuildable
  on any machine without that drive - including CI - and it put a second drive-letter path in a tracked
  file, which the store's own rules forbid precisely because a clone cannot resolve one. Both are gone: the
  test that read the document was deleted with it, because what it was really asserting - the `FMSBCAST1`
  prefix, the `schemaVersion` refusal, one connection per listener - is asserted against the code that
  implements those shapes and not against a document nobody executes. The pattern to keep: contracts are
  **cited**, never copied and never compiled against; `docs/contracts/*.md` holds the pointers, and
  `CLAUDE.md` is the single place naming where the store is.

- **`UserAuthoredChannels.Identify` is a list that has to grow, and nothing enforces it.** Since SP-0089
  a catalog refresh no longer deletes a row just because the bank stopped listing its URL - it deletes
  only rows carrying nothing the user made, and retires the rest (`StreamChannel.RetiredAt`, id kept so
  collections and history stay attached). The whole rule rests on one enumeration in
  `src/StreamsPlayer.Core/UserAuthoredChannels.cs`: pin, collection membership, history entry. **Any new
  feature that attaches a user-made value to a channel must be added there**, or a refresh will delete it
  the first time the producer's liveness probe misfires - which is not hypothetical: on 2026-08-19 the
  bank dropped 1 906 rows, 79 % of them on an `unknown` verdict and 1 321 of them still playing the next
  day. There is no compiler error and no failing test for forgetting; the cost is a user losing something
  they made. Deliberately excluded, and each for a reason worth re-reading before "fixing" it: hidden
  URLs (kept by normalized URL in their own list, and keeping the row would contradict the request),
  quality memory (by URL, own file, a cache), and `LastPlayedAt`/`LastPlayOutcome`/`SortIndex` (things
  the application wrote about itself, not things the user made).

- **The App's data directory cannot be redirected, so GUI state cannot be faked for observation.**
  `AppPaths` uses `Environment.SpecialFolder.LocalApplicationData`, which on Windows resolves through
  `SHGetFolderPath` and **ignores `%LOCALAPPDATA%`** - setting the environment variable for a child
  process does nothing. The tempting workaround, swapping `catalog-state.json` for a crafted one, is
  worse than it looks: `StreamCatalogStore.SaveAsync` prunes atlas files the state no longer references,
  so a crafted state without `AtlasFileName` makes the first save delete the owner's real
  `favicon-atlas-<guid>.png`, and only a full re-download brings the icons back. Consequence for the
  evidence rule: a visible behaviour that only appears on state the live bank does not produce is
  **not** observable on this machine, and the honest move is to say so in the ticket rather than
  manufacture the state. Read-only measurement against the real state is fine and is the right
  substitute - load the built `StreamsPlayer.Core.dll` with `Add-Type`, call the product's own
  `StreamCatalogStore.LoadAsync` / `CatalogMerger.Merge`, inspect the result in memory, write nothing
  (`artifacts/sp0089-measure.ps1` is the worked example; `artifacts/` is gitignored).

- **The house version stamp cannot be handed to a version-resource field, and the failure is silent.**
  `YY.MMDD.HHmm` (`26.0820.1828`) is a *string* shape whose middle field is zero-padded. Anything that
  wants a numeric dotted quad - Inno Setup's `VersionInfoVersion`, and the same trap exists in MSI/WiX -
  parses `0820` as `820`, so the artifact ends up stamped `26.820.1828` while the application it
  installs reports `26.0820.1828`. Nothing errors; the two versions simply disagree forever, and the
  installer's Programs-and-Features entry is the one users see. SP-0092 sets that field deliberately and
  separately, with the reason written in `installer/StreamsPlayer.iss`. The general rule: the house stamp
  is display metadata, and any field that demands numeric components needs its own value, chosen once and
  non-decreasing. (This is the same padding hazard `build-msix.ps1` already solves by int-casting each
  component - the MSIX remap was not a one-off quirk, it was the first instance of a recurring class,
  2026-08-21.)

- **Adding a fourth install channel is mostly a copy-deck and overlay job, not a build job.** SP-0092
  shipped the Inno installer without touching `src/` or `tests/` at all: the release workflow already
  staged a complete self-contained tree in `stage/StreamsPlayer` for the ZIP, so the installer is a
  second consumer of that same staging directory - which is also what guarantees the two assets can
  never carry different payloads. The expensive parts were elsewhere: thirteen copy decks gated on key
  parity (the generator throws before writing, naming every deck that lags), `docs/style.css` hardcoding
  `repeat(3, ...)` for the channel grid, and `AGENTS.md` plus the canon's `contrib/streams_player.md`
  both *declaring* the repo installer-free in several places at once. Check those declarations before
  assuming a distribution change is small (2026-08-21).

- **A runtime patch can break the product's core function, and a green build says nothing about it.**
  WPF runtime **10.0.11** breaks `MediaElement` network audio: every station fails instantly with
  `InvalidOperationException` out of `MediaFailed` while the server answers `200`. The app opens, loads
  the catalog, renders the grid - and plays nothing. `scripts/check.ps1` stayed 858/858 green throughout,
  because nothing in the repo was wrong. SP-0093 pinned `Microsoft.WindowsDesktop.App` to 10.0.10 in
  `Directory.Build.targets`; SP-0133 removed the pin (2026-09-26) once radio had left `MediaElement` for
  LibVLC (SP-0104) and the smoke gate proved sound and picture on 10.0.11 itself. **The published 26.0820.1828 shipped with 10.0.11 and is affected** - it was
  released before anyone played a stream from it.
  Three durable lessons, each of which cost a wrong turn here:
  1. **Isolate by swapping one variable under an unchanged artifact.** Overlaying only the WindowsDesktop
     runtime DLLs onto one already-published build settled it in minutes, after version-vs-version and
     packaging-vs-packaging comparisons had produced a confident and wrong theory (they were confounded:
     the only build that worked was both older *and* single-file).
  2. **`Get-Process().Modules` is the cheap oracle for native-stack failures.** The broken build stops
     after `MFPlat.DLL` and never loads `mfnetcore.dll`, the Media Foundation *network* source; a working
     one loads the whole `MFCORE`/`mfnetcore`/`mfsrcsnk` chain. That diff named the layer immediately.
  3. **Verify a version pin by reading the version out of the publish output, never by the build
     succeeding.** `RuntimeFrameworkVersion` metadata on a `FrameworkReference` item is *silently
     ignored* - it builds clean and ships the unpinned runtime anyway. And the global
     `RuntimeFrameworkVersion` property cannot be used here at all: it is inherited by
     `Microsoft.Windows.SDK.NET.Ref`, whose versions look like `10.0.19041.57`, so restore dies `NU1102`
     hunting a "10.0.10" of it. The pin has to update `KnownFrameworkReference`, which only exists after
     the SDK targets - hence a root `Directory.Build.targets` (2026-08-21).

- **`project` - a release and the site that advertises it must be two pushes, in that order.**
  `pages.yml` deploys on any push to `main` touching `docs/**`, so a single commit carrying both the
  release wiring and the generated site publishes the download tile *before* the release that contains
  the file - the tile would resolve to nothing for the minutes the release workflow runs, and to the
  previous release forever if the workflow failed. Split it: commit everything except `docs/`, push,
  tag, wait for the release to land and verify the asset, then commit `docs/` and push. Since SP-0156
  the deploy waits for CI's documentation and site-sync gates on the same commit (a `ci-gate` job), so
  the split leaves no red site in between - the only cost is remembering to run `build-site.ps1` and
  make the second commit (2026-08-21; gate added 2026-09-30).

- **`reference` - launching a station from the command line takes `--url <value>`, two arguments.**
  `StreamLaunchRequest.Parse` accepts exactly zero or two arguments; anything else is `Invalid`, and an
  `Invalid` launch is *silent* - the app opens and loads the catalog normally and simply never plays.
  A bare URL is therefore not a weaker form of the same thing, it is a no-op that looks exactly like the
  SP-0093 audio failure: no `AUDIO` line in `Current.log` at all. When verifying playback, the
  distinction is `AUDIO OPEN` present-and-then-failing (a real fault) versus no `AUDIO` line whatsoever
  (a mis-typed invocation). Also allow ~30 s for the catalog to load before playback is even attempted;
  killing the process at 30 s reads as a failure too (2026-08-21).

- **`project` - "channel unchanged" and "channel fine" are different claims.** SP-0093 shipped a total
  playback failure to 26.0820.1828, and the owner's instinct was to leave winget and the Store alone
  because their manifests had not changed. The manifests had not; what they *delivered* had. winget
  resolved to the broken build and was handing people a silent application, while the Store sat on
  26.0806.2225 - a version predating the bad runtime entirely, so genuinely fine, just old. **The two
  channels needed opposite answers, and neither answer was derivable from "did we touch it".** Decide
  per channel by asking what artifact it actually serves and what runtime that artifact carries, then
  say which channels are in the broken set and which are not - and say it in
  `msix/store-listing.md`, where the next submission will read it (2026-08-21).

- **`reference` - `LocalManifestFiles` was already enabled on this machine, so the winget install test
  is available.** Three submissions went out with the `winget install --manifest` box unticked because
  the note in `winget/README.md` said enabling it needs elevation. It was already on. Check
  `winget settings export | ConvertFrom-Json` -> `adminSettings.LocalManifestFiles` before believing a
  note that says a check is impossible - a note records what was true once, and the cheapest possible
  probe is one command. The full ladder now runs: install from the manifest, launch the payload with
  `--url` against a live station, `winget uninstall`, confirm `%LOCALAPPDATA%\StreamsPlayer` survived
  (2026-08-21).

- **`project` - `msix/build-msix.ps1` packs the working tree, not the tag.** `release.yml` builds the
  zip and the installer from a fresh checkout of `v<version>`, so those assets cannot pick up
  uncommitted work; the MSIX has no such protection - it runs `dotnet publish` against whatever is on
  disk and then stamps the version you passed on the result. On 2026-08-21 that produced a
  "26.0821.1208" package carrying 1,502 bytes of an unreleased feature another session was editing in
  the same tree, three minutes before the pack. Caught only by `git status` before committing, not by
  anything in the build. **Build a Store package from `git worktree add ../<dir> v<version>`** unless
  the tree is provably clean at the tag, and delete a contaminated package instead of keeping it - a
  wrong package that looks right is worse than none.

- **`project` - this working tree can have another agent session editing it concurrently.** `git add -A`
  staged twenty-one files of someone else's in-progress SP-0094 work along with mine. Stage by explicit
  path when the tree is shared, and read `git status` before every commit rather than trusting that the
  only changes present are the ones you made (2026-08-21).

- **`project` - WPF 10.0.11 blocks Internet-zone media on purpose, and ships an undocumented switch to
  turn it off.** `MediaPlayerState.OpenMedia` gained a default-credentials **zone policy**: any
  `http(s)` URI outside Local/Intranet/Trusted is refused before Media Foundation is touched, because
  the native media pipeline attaches system credentials and managed code cannot suppress them. The
  escape hatch is `Switch.System.Windows.Net.DoNotApplyZoneCheckForDefaultCredentials`, set through
  `RuntimeHostConfigurationOption` - verified to restore playback on 10.0.11 with the whole MF chain
  loaded. It has **zero** hits in GitHub issues and `dotnet/docs`, so it is not findable by search.
  Reported as [dotnet/wpf#11856](https://github.com/dotnet/wpf/issues/11856).
  **The lesson is about the diagnostic, not the policy.** The block reuses
  `Media_PackURIsAreNotSupported` - "Only site-of-origin pack URIs are supported for media" - for an
  ordinary `https://` URL. A borrowed error message does not merely fail to help, it actively
  misdirects: it cost this investigation a day of bisecting runtimes and produced a shipped fix (a
  version pin) that was worse than the real one, because the message never hinted the cause was policy
  rather than a broken runtime. **When an error message describes a thing that is not present in your
  program at all, suspect the message before you suspect your understanding** - and go read the throw
  site in the source rather than reasoning from the text (2026-08-21).

- **`reference` - a leftover StreamsPlayer process hides a `--url` launch from `Current.log`.** The
  symptom is a `Current.log` full of ordinary catalog activity and **no `AUDIO` line at all** - identical
  to what a mis-typed invocation looks like, and easy to misread as the playback bug itself.
  `Get-Process StreamsPlayer` and kill before any run-and-observe check (2026-08-21).
  **Correction, 2026-09-25: the app is NOT single-instance, and this entry's original explanation ("it
  hands off") was wrong.** Nothing in `src` holds a mutex or a pipe; the second process runs as a full
  second instance. It is missing from `Current.log` because the first process still owns that file, so
  the SP-0085 path in `CurrentLog` gives the newcomer its own `Session-<stamp>.log` - look there. Two
  instances also overwrite each other's whole state and delete each other's atlas; SP-0118 makes the
  app single-instance per `APP-ACTIVATION`. Verified by reading `App.xaml.cs` and `CurrentLog.cs:45-57`.
  **Update, 2026-09-25, SP-0118 implemented: builds from this date ARE single-instance per session.**
  A second launch forwards over the pipe, exits 0 in ~0.2 s and writes no log at all; the running copy
  logs `LAUNCH FORWARDED | kind=.. | foreground=..` and plays the target. So on a current build the
  `--url` lands in the *running* copy's `Current.log`; on an older running build the old
  `Session-<stamp>.log` explanation still applies. Killing stale copies before a check stays the rule.
  A running copy that holds the lock but does not answer yields a dialog and exit 1 - to reproduce it,
  hold `Local\StreamsPlayerSingleInstance` from PowerShell without listening.

- **`project` - the release gate that unit tests structurally cannot be: `scripts/smoke-playback.ps1`.**
  Added 2026-08-21, after SP-0093 shipped a release that built clean, passed 858 tests and played
  nothing. It publishes the tree and plays a live radio station (WPF `MediaElement` then; the audio-only LibVLC
  engine since SP-0104) and a live video stream (LibVLC) through the shipping binary; `release.ps1` step 2b, rung 8 of the validation ladder.
  It is deliberately outside `check.ps1`, which must stay offline and deterministic for CI.
  **The reason is the generalizable part: tests cover the code you wrote, and the thing that broke was
  something the build merely consumes.** No amount of unit testing reaches it. When a product depends
  on a runtime, a native library or a service, the only evidence it works is that it worked.
  **A gate nobody has watched fail is not a gate.** This one was proved in both directions before being
  committed: green on the released build, and red - exit 1, both stations, the right diagnosis printed
  - on a deliberately unpinned build carrying the broken runtime. Do that for every new gate; the
  negative control is the cheap half and the only half that proves anything.

- **`reference` - a WPF window's placement is verifiable without a human.** SP-0084's acceptance was
  seven criteria about where a window lands, and the repo's rule is that a changed GUI action needs
  run-and-observe evidence. The whole set was checked from PowerShell: launch the shipping binary with
  `--url <stream>`, `EnumWindows` filtered by pid for the visible top-level windows (the player is the
  one titled `<host> - STREAMS Player video`; the catalog is `STREAMS Player`), then `GetWindowRect` to
  read the placement, `MoveWindow` to drag and resize it, and `WM_CLOSE` (0x0010) to close it *properly*
  so `OnClosing` runs - `Stop-Process` does not, and a harness that kills the process proves nothing
  about what closing records. `WM_SYSCOMMAND`/`SC_MAXIMIZE` (0x0112, 0xF030) covers the maximized case.
  Seeding the state file by hand is what makes the untestable cases testable: a rectangle at 6400,-3000
  is a monitor that does not exist, and no second screen has to be unplugged to check it (2026-09-09).

- **`project` - `dotnet` is on PATH in the PowerShell tool and not in the Bash one.** `./scripts/check.ps1`
  invoked through Bash dies with "The term 'dotnet' is not recognized" - which reads exactly like a
  broken script or a missing SDK, and is neither. Run every build, test and gate through the PowerShell
  tool. Unrelated to the canon's `guard-bash` rule about routing `.ps1` through an interpreter; this one
  bites even when that rule is followed (2026-09-09).

- **`project` - `PLAN/` is gitignored in this repository.** Tickets are working-tree state, never
  committed, which is the mechanical reason `AGENTS.md` insists status comes from the tree and never
  from git history - there is no history to consult. A `git add PLAN/...` is refused as an ignored path;
  do not reach for `-f`. It also means a ticket's status edit is invisible to a reviewer reading the
  diff, so a commit message has to carry the evidence a reader would otherwise look for in the ticket
  (2026-09-09).

- **Two tooling traps from SP-0109 (2026-09-23), each one debugging round.** (1) In a PowerShell
  single-quoted string the typographic apostrophe `’` (U+2019) is a *closing quote* - PowerShell treats
  `‘ ’ ‚ ‛` as single quotes - so a Ukrainian value like `прев’ю` in a translation table is a parser
  error. Keep a placeholder in the script and substitute `[char]0x2019` at write time, or read the
  strings from a data file. (2) Git Bash `grep -c $'\r'` does not count CRLF lines reliably on these
  files; count `\r\n` against lone `\n` with `[regex]::Matches` in PowerShell before trusting a
  line-ending diagnosis. The dictionaries are mixed today (en/ru LF in HEAD, the other eleven CRLF, and
  peer sessions rewriting some to LF), so an insert script must take each file's dominant ending.
- **Peer sessions share this working tree and the real `%LOCALAPPDATA%\StreamsPlayer`.** On 2026-09-23
  three StreamsPlayer sessions ran at once; one edited `SettingsWindow` and `MainWindow.Settings.cs`
  between this session's read and write, and one moves the data folder aside for its GUI runs. Before a
  GUI run-and-observe, `ListAgents` and claim the slot by message; address the app by PID only; and
  re-read any shared file immediately before editing it. `temp/SP-0109/observe.ps1` is a reusable
  sandboxed run (copies the catalog state, forces list view, restores the real folder in `finally`).
- **After a LibVLC player window closes, UI Automation stops seeing the catalog window's content.** Found
  2026-09-25 (SP-0075): the window renders normally, but its UIA tree shrinks to the title bar, and a
  fresh app instance inspected from the *same* pwsh UIA client stays truncated too. A mouse click to wake
  the player's auto-hidden panel is also unsafe - it lands on whatever window is in front. What worked:
  read the player's panel by polling in the first seconds after it opens (it is shown on start, then
  hides), and run every step that follows a player in a *new* pwsh process, seeded from a file the
  earlier part saved. `temp/SP-0075/observe.ps1 -Part AB|CD` is the worked example.
