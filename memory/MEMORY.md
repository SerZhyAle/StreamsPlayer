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
