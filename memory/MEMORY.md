# StreamsPlayer Agent Memory

Short index of durable, non-obvious context for future sessions. Add one link per entry; keep entry bodies in separate files and verify repo claims before relying on them.

## User

## Feedback

- When a task statement is meaningfully ambiguous, ask the user to clarify it
  before choosing an interpretation that could change the expected result.

- **A pull request into someone else's repository uses that repository's PR template verbatim.**
  Fetch `.github/PULL_REQUEST_TEMPLATE.md`, keep its headings, its wording and its checklist exactly
  as written, put an `x` in the boxes that are genuinely true, and add prose only in the section the
  template designates for it. Do **not** write a body of your own structure, and do not paraphrase a
  checklist from memory - the maintainer bots parse the template, so a rewritten one reads as an
  unfilled one. The same applies to the **title**: the template states the required format, and
  inventing a variant gets the PR misclassified.
  The owner has corrected this repeatedly - it is a recurring failure, not a one-off. Concrete cost
  on 2026-07-27: PR microsoft/winget-pkgs#408215 was opened with a hand-written body and the title
  "New version: SerZhyAle.StreamsPlayer version 26.0727.0253" when the template requires
  "Update: Publisher.Name to X.Y.Z". Result: labels `Policy-Test-2.7`, `Validation-Guide` and
  `New-Manifest` - an already-published package classified as a brand-new one - and a validation
  complaint, despite the manifest itself being correct and the Azure pipeline passing.
  This overrides the house rule about the Claude Code footer in PR bodies: that rule is for this
  project's own PRs, and a third-party template wins over it.

## Project

- **The colour theme only works because every palette reference is a `DynamicResource`.** `ThemeService`
  (App layer; Core only stores the `AppTheme` enum) recolours named brushes in
  `Application.Current.Resources` at runtime, so a single `StaticResource` on a palette key silently opts
  that element out of live switching - it will look right at startup and stop following the theme
  afterwards. The System mode reads `HKCU\...\Themes\Personalize\AppsUseLightTheme` and subscribes to
  `SystemEvents.UserPreferenceChanged` **only while System is selected**, unsubscribing in `App.OnExit`;
  a permanent subscription is a process-wide leak. Two known rough edges left in place: `Initialize()`
  applies the system theme before the saved choice is read (a brief flash when an explicit Light sits on a
  dark system), and the main window's status bar is a hardcoded dark colour that predates the ticket
  (SP-0046, 2026-08-06).
- **`build.ps1` deploys by default.** `-Deploy` is `$true` unless you pass `-Deploy:$false`, and when it is
  set the script *forces* Release + win-x64 and throws on `-Configuration Debug`. So a bare
  `./build.ps1 -Test` is not a Debug test run: it builds Release, tests, then publishes a self-contained
  single-file EXE into `C:\GD\i` and `C:\GD\tc\SZA\_APP`. `./run.ps1` is the safe launcher - it always
  passes `-Deploy:$false`. Both `CLAUDE.md` and `AGENTS.md` had documented the opposite for months
  (corrected 2026-08-06).
- **A failed state save used to kill the whole app.** Every save path is an `async void` handler and
  `App_DispatcherUnhandledException` logs without setting `e.Handled`, so one `IOException` from
  `StreamCatalogStore` ended the process - the window simply vanished, and on a Store install the log
  was out of reach. The volume slider exposed it because it fired one whole-catalog write (2.9 MB) per
  pixel of travel, so a scanner or the MSIX redirector holding `catalog-state.json` for a moment was
  near certain. Reproduced by locking the state file with `FileShare.None` while dragging the slider:
  `Unhandled WPF dispatcher exception: UnauthorizedAccessException at File.Move`, no
  "Application shutdown." line. `MainWindow.PersistAsync` now absorbs and logs I/O failures - keep new
  saves funnelled through it, and debounce any control that can fire it continuously (2026-07-31).
- **The README trio is the product's manual, not repo prose.** Settings -> Instructions opens
  `README.md`, `README.ru.md` or `README.uk.md` by interface language
  (`ProductInfo.InstructionsUrl`), so a UI change is not finished until all three describe it.
  Nothing gates the mirrors: the Russian file had silently lost listening history and the whole
  M3U import/export section, and the English text still called ICY metadata a "later milestone"
  long after it shipped (2026-07-28).
- **Figures quoted in prose had never matched the code.** "10-second live buffer" (really 15 s,
  4 s when a stalled stream is re-opened - `PlayerWindow`) and "the latest 64 frames" (really a
  150 MB disk budget - `PreviewFrameStore`) had propagated into the README trio, all thirteen
  `tools/site/copy/*.txt` decks and the privacy pages. A number in prose has no test behind it:
  when auditing docs, re-read the constant instead of trusting the sentence (2026-07-28).
- **`tools/site/build-site.ps1 -Check` false-fails on a fresh Windows checkout.** `.gitattributes`
  declares `*.html eol=crlf` while the generator writes LF, so its byte comparison reports all 27
  generated files stale when the content is identical - `git diff` after a regeneration is what
  actually tells you whether the site changed (2026-07-28).
- Catalog text search (`ApplyFilter`, `MainWindow.xaml.cs`) intentionally matches
  Title **OR** Topic **OR** Language. Channels whose *category/topic* matches (e.g.
  "Sports") appear even without the term in their name - this looks "unfiltered"
  but is by design. User confirmed keeping the broad match (2026-07-20). Do not
  narrow it to name-only without a new product decision.
- After a strategic `PLAN/SP-NNNN_*.md` ticket reaches `Verified`, move that
  ticket file and its same-named tactical-plan folder, when present, to
  `PLAN/DONE/`. Keep active, blocked, Draft, Approved, Tactical, In Progress,
  Implemented, Partial, and Broken tickets in `PLAN/`; update any affected
  local links when moving a verified ticket.

- Live recovery (SP-0015): the retry policy is a pure Core state machine
  (`LivePlaybackRecoveryPolicy` + `PlaybackRecoveryClassifier`); App backends feed
  `PlaybackFailureSignal` and apply decisions. Three non-obvious design points to preserve:
  (1) LibVLC and WPF `MediaElement` hide the HTTP status, so 429/5xx-vs-non-429-4xx classification
  needs a failure-path-only probe (`PlaybackStatusProbe`, http/https only, never on grid previews);
  (2) budgets are *consecutive* and reset on sustained live (`NotifyLive`), which is what keeps
  looping-playlist EndReached streams from exhausting the budget - do not make budgets lifetime;
  (3) Part D's stall-watchdog and the tuning-doc rule "never reconnect to grow the buffer" are
  reconciled by reconnecting only on a *silent freeze* (position frozen ~9 s while nominally playing,
  gated on `_reachedLive`) or buffering > 15 s with no position progress - genuine rebuffering is left
  in place. See `PLAN/SP-0015_resilient_live_recovery.md`.

- **CyrFlip is the portfolio's 13-language precedent** (owner's machine:
  `P:\WINDOWS\CyrFlip`). It already ships a 13-language UI, site, Store listing and one
  screenshot per language, using the set `en ru uk de it es fr pt-br zh-hans hi bn ar ur`.
  `msix/README.md` there records the Partner Center failures already paid for - the export
  must be re-taken before every import, additional languages must be added by hand or their
  copy is dropped *silently*, the import is all-or-nothing, relative image paths work only
  via folder upload, the Win10 logo-override flag must never be copied between languages, a
  listing without a screenshot stays Incomplete with no error shown, and Partner Center
  refuses its own export's BOM. StreamsPlayer's `tools/store/merge-listing-csv.ps1` writes
  `utf8BOM` and overwrites cells unconditionally - both known-bad there. Read that file
  before any Store-listing or multi-language work; do not re-derive it. See
  `PLAN/SP-0034_thirteen_language_interface.md`.

- The upstream catalog contract is documented in the FastMediaSorter repo at
  `delivery/stream-catalog/README.md` (owner's machine:
  `P:\ANDROID\FastMediaSorter_mob_v2\delivery\stream-catalog\README.md`). It is the authority for the
  bank we consume and it changes without an app release - re-read it before touching catalog parsing.
  Two consumer-side couplings it drives: (1) `StreamBankReader.MaximumAtlasBytes` must track the
  publisher-side ceiling (raised to 30 MiB on 2026-07-26; the live atlas was already 2.9 MB against
  the old 4 MB limit), and (2) columns are added upstream silently - `access` (values: empty or `geo`,
  region-restricted, deliberately kept in the bank) shipped before we parsed it. Confirmed 2026-07-26.

- **A second agent session can be writing to this tree at the same time.** On 2026-07-26 two sessions
  independently allocated `SP-0031` (channel preview atlas, and the region-lock hint) and edited the same
  files; one `Edit` reported "the file had been modified on disk since you last read it". Consequences to
  guard against: allocate an `SP-NNNN` by re-scanning `PLAN/` **immediately** before writing the file, not
  from a scan made earlier in the session; and before reporting a diff, run `git status` and separate your
  own changes from the other session's rather than describing the whole working tree as yours. The clash
  was resolved by renumbering the later, still-in-progress ticket (region lock → SP-0033).

## References

- Toolbar glyph icons: `App.xaml`'s shared `GlyphButton` template applies **both**
  `Fill` and `Stroke` = Foreground to the swapped `GlyphGeometry`, so any closed/near-closed
  path renders as a solid silhouette (fine for a gear or eye, wrong for an outline shape like
  a clock face whose hands would vanish). For an outline icon, give the style its own
  `ContentTemplate` with `Fill="Transparent"` instead of only swapping `GlyphGeometry` -
  see `HistoryGlyphButton` (SP-0019). Confirmed 2026-07-22.

- GUI run-and-observe without a human: drive the app from PowerShell with
  `Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes` and screenshot with
  `System.Drawing` `CopyFromScreen`. Three traps found 2026-07-24 (SP-0030): (1) `PlayerWindow`,
  `SettingsWindow`, and `MessageBox` are **descendants** of the main window in the UIA tree, not
  root children - `FindFirst(TreeScope.Children, ...)` on the desktop only ever returns
  `Трансляции`/`STREAMS Player`; (2) Settings tabs expose no usable `Name` (headers are
  StackPanels) - select by index via `SelectionItemPattern`; (3) `ShowDialog` disables the other
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

- `StreamCatalogStore.SaveAsync` calls `RemoveUnreferencedAtlases` on **every** save, deleting any
  `favicon-atlas-*.png` that the just-saved state does not name. Consequence when testing against
  the real `%LOCALAPPDATA%\StreamsPlayer` state: a backup copy of `catalog-state.json` restored
  after a refresh points at an atlas file that no longer exists (icons go blank; no crash - the
  loader `File.Exists`-guards). Repair is one explicit **Update catalog**, which merges by URL and
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

- **GUI evidence via UI Automation: address controls by `AutomationId`, which WPF fills from `x:Name`** -
  language-independent, unlike `AutomationProperties.Name`. Two traps measured on SP-0040: a modal dialog
  is not in the UIA tree the instant `Invoke` returns (poll for one of its children instead of sleeping),
  and `TabControl` content for unselected tabs does not exist at all, so the tab must be selected before
  its controls can be found. `LanguageWindow`'s list was not reachable this way at all; capturing a
  right-to-left window is cheaper by swapping the single root `"language"` token in `catalog-state.json`
  and restoring it afterwards - never by a JSON round trip, which would rewrite 2.9 MB of the owner's
  real catalog (2026-07-30).

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
  is normal: the second is the WPF `MediaElement` stack's own request (appears ~8 s after playback
  starts, gone at stop, unaffected by the setting - see SP-0051). A `DISPLAY x1 + SYSTEM x1` residue
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
  ... without `workflow` scope" whenever upstream `winget-pkgs` has touched `.github/workflows/`, which
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
