# StreamsPlayer Agent Memory

Short index of durable, non-obvious context for future sessions. Add one link per entry; keep entry bodies in separate files and verify repo claims before relying on them.

## User

## Feedback

- When a task statement is meaningfully ambiguous, ask the user to clarify it
  before choosing an interpretation that could change the expected result.

## Project

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
