---
name: project_general
description: Cross-cutting project learnings, corrections, and non-obvious context
 type: project
---

# Project General Learnings

## Project

- **Two traps in writing redaction/timeout tests (SP-0174, 2026-09-29).** First, `.gitignore` carries
  `*secret*` (leak pre-emption, line 38) - a test file named `*Secret*` is silently ignored and never
  committed; name such tests "Audited"/"Credential" instead. Second, a Regex match timeout cannot be
  forced deterministically by a tiny budget: the constructor rejects `TimeSpan.Zero`, and a 1-tick budget
  still never fires on a short line because the engine checks the clock only between match attempts - the
  reliable recipe is size asymmetry (a 1 ms budget; a multi-MB line cannot scan that fast, a short line
  cannot help finishing). Related line-shape fact: `text.Split('\n')` attaches a CRLF pair's `\r` to the
  *end* of the preceding element, so a marker that replaces a timed-out line must re-append its `\r` or the
  marker glues two log lines into one.

- **The FFmpeg natives FlyleafLib publishes are GPLv3, so they can never be bundled.** The `FFmpeg`
  folder inside `Flyleaf_v3.10.4.7z` is built `--enable-gpl --enable-version3` with libx264/libx265;
  its `avutil` reports `GPL version 3 or later`. FlyleafLib *itself* is LGPL-3.0, which is what made
  SP-0026's original "ship both native stacks" decision look safe - the licence trap is one layer
  down, in the binaries upstream tells you to fetch. StreamsPlayer therefore downloads an **LGPL**
  build instead (`BtbN/FFmpeg-Builds`, `win64-lgpl-shared-8.1`, pinned to a month-end `autobuild-*`
  tag with its length and SHA-256 in `FFmpegComponentsInstaller.PinnedSource` - SP-0128; BtbN prunes
  daily tags but keeps month-end ones, and `latest` moves daily so no digest can pin it), on explicit user
  request, into `%LOCALAPPDATA%\StreamsPlayer\FFmpeg` - never into the package. Two facts that make
  this work and are not obvious: both builds export the *same* sonames (`avcodec-62`, `avformat-62`,
  `avutil-60`, `swresample-6`, `swscale-9`, `avfilter-11`, `avdevice-62`), so `Flyleaf.FFmpeg.Bindings
  8.0.1` binds to either; and the LGPL asset is a `.zip` while Flyleaf's is a `.7z` the framework
  cannot open. Also note `Flyleaf.FFmpeg.Bindings` is pinned to **8.0.1 against FlyleafLib's nuspec
  dependency of 7.1.1** - that is deliberate and upstream-documented ("use Flyleaf.FFmpeg.Bindings v8
  at your project"), not a mistake to "fix" (SP-0026 Phase 6, 2026-08-07).
- **The player's overlay leaves the window's inheritance chain, so inherited properties must be set on it
  explicitly.** `PlayerWindow` detaches `ControlsOverlay` in its constructor and hands it to
  `IVideoBackend.SetOverlay`, which makes it the `Content` of LibVLC's `VideoView` / Flyleaf's
  `FlyleafHost` - and both present that content on a **separate foreground window** stacked over the
  native video surface. That is what keeps the controls above the video through resizes (airspace), and
  it is also why WPF property inheritance from `PlayerWindow` stops at the boundary: the overlay's new
  ancestor is that foreground window, not the player window. `FlowDirection="{DynamicResource
  UiFlowDirection}"` on the window therefore never reached the control panel, which rendered
  left-to-right in Arabic and Urdu for as long as the overlay has been reparented. Fixed by binding it on
  `ControlsOverlay` itself (SP-0072, 2026-08-08, verified by running the app in Arabic). Anything else
  inherited - `DataContext`, `FontFamily`, `TextElement` properties - has the same hole; set it on the
  overlay, not on the window.
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
  `./build.ps1 -Test` is not a Debug test run: it builds Release, tests, then publishes the release-shaped
  self-contained folder into `C:\GD\i\StreamsPlayer\` and `C:\GD\tc\SZA\_APP\StreamsPlayer\` (SP-0133; a
  single-file EXE in the roots before that). `./run.ps1` is the safe launcher - it always
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
- **UI events keep arriving after `MainWindow_Closed` has started disposing things.** Two mechanisms,
  both observed, not inferred (SP-0065, 2026-08-08): the handler is `async void` and awaits a session save,
  so the dispatcher pumps input in the middle of the teardown; and destroying the hwnd disposes
  `HwndMouseInputProvider`, which synthesizes a final `MouseLeave` for whatever the pointer was over.
  Measured ordering in one log: `Closed` body → `SaveBrowsingSessionAsync` → `MouseLeave` →
  `Application shutdown.` - so the leave lands **after** the disposals in that same handler, which is how
  `StreamTile_MouseLeave` came to call `Cancel()` on a disposed `CancellationTokenSource` and take the
  process down. Consequences to keep in mind for any teardown work: an unhandled exception here skips
  `App.OnExit`, and that is where `WakeGuard.Reset()` lives, so the crash could leave a power request
  behind. **Disposing a field is not enough - null it, and gate the handlers.** `MainWindow._shuttingDown`
  is the latch and `GridPreviewCoordinator` carries its own `_disposed` flag because it owns the semaphores
  that a late `StartAsync`/`StopAsync` would wait on. Closing the catalog also closes every open
  `PlayerWindow` - explicitly, through `MainWindow.CloseOpenPlayerWindows()`, since a player is a top-level
  window and no longer WPF-owned by the catalog - and their `Closed` handler calls back into
  `StartPreviewsAsync`, so the late caller is not always an input event. *Corrected 2026-09-25 (SP-0120):*
  the latch used to be set in `Closed`, which runs *after* `Closing` has closed the players, so the last
  player's callback restarted preview capture during shutdown; it is now the first statement of
  `MainWindow_Closing`. The coordinator re-checks `_disposed` after taking its lock and no longer disposes
  its semaphores - a caller already queued on one used to wait for ever. The close work is a `Task`
  (`MainWindow.CloseWork`) and the process ends only when it finishes or 4 s pass (`ShutdownMode` is
  `OnExplicitShutdown`; `App.EndAfterCloseWorkAsync`, `App.OnSessionEnding`).
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
- A strategic `PLAN/SP-NNNN_*.md` ticket moves to `PLAN/DONE/` with its
  tactical folder once it reads `Verified`, `Archived`, `Implemented` or
  `BlockNeedUserTest` (owner decision 2026-09-26: implemented-and-awaiting-test
  work leaves the queue folder). Draft, Approved, Tactical, In Progress,
  Partial, Broken and the other Block* states stay in `PLAN/`; update any
  affected local links when moving. Rule home: `docs/agent/SPEC_LIFECYCLE.md`.

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

- **CyrFlip is the portfolio's 13-language precedent** (a sibling repository on the owner's machine,
  beside this one). It already ships a 13-language UI, site, Store listing and one
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

- The upstream catalog contract is **`STREAM-BANK`, in the shared contract store** (`Contracts/stream-catalog/`,
  located once in `CLAUDE.md`; pointer in `docs/contracts/STREAM-BANK.md`). Until 2026-09-22 this entry
  named the producing repo's own `delivery/stream-catalog/README.md` as the authority; that is no longer
  true - the producer's README now points at the store rather than restating it, which is what the store
  exists for. It is the authority for the bank we consume and it changes without an app release - re-read
  it before touching catalog parsing.
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
  Confirmed again 2026-08-08: SP-0059 landed in `MainWindow.xaml.cs`, `Localization.en.xaml` and the
  README trio *while* SP-0058 was being implemented in the same files. Nothing was lost, because both
  changes were additive and every edit went through `Edit` (which fails on a stale read) rather than a
  whole-file rewrite. The practical rule that follows: prefer `Edit` over `Write` on any file another
  ticket might be touching, and re-run the gates at the end rather than trusting a result from before.

- **The bank folded `topic` into a closed set of 32 rubrics, and told nobody.** Measured on the live
  artifact 2026-08-07: 19855 rows, 31 distinct values, zero blanks (`Test` is declared and unused).
  The upstream contract document still describes `topic` as free text with examples that no longer
  occur ("Jazz", "Lo-fi", "Science & Space") and still reports a 2361-row bank - so for this column
  **the data is the authority and that README is stale**, which is the standing "columns change
  silently" warning arriving a second time. Two consequences worth keeping: the vocabulary is a Core
  registry (`CatalogTopics`) that maps an identifier to a localization key and answers `null` for
  anything else, so an unknown rubric is displayed rather than dropped; and `Traffic cams` (881 rows)
  is entirely `is_live=false` while `Webcam` (140) is entirely `is_live=true` - the split exists so a
  client does not promise a broadcast that is really a clip re-posted every few minutes (SP-0061).

- **A localization dictionary edit needs an App rebuild before any GUI check.** `Localization.*.xaml`
  compiles into the App assembly, and `dotnet test` builds only Core and the test project - so a UIA
  pass straight after a dictionary change renders every new string as its own key name
  (`TopicAdult`, `TopicPop`). That looks exactly like a missing key and sends you into the wrong file.
  Cost one full sandboxed run on 2026-08-07 (SP-0061).

- **CLDR's `ru` collation reorders Cyrillic ahead of every other script**, so a label written in Latin
  ("R&B и соул") correctly sorts *after* the whole Cyrillic block in a Russian list. Reading that as a
  sorting bug is the easy mistake - it is what a Russian reader expects, and it only appears once a
  list is ordered by `StringComparer.Create(CurrentUICulture, ..)` instead of ordinally (SP-0061).

- **`CatalogState` serializes `Channels` before `Language`, so the root `language` token is the *last*
  `"language"` match in `catalog-state.json`** - every earlier one belongs to a channel. The documented
  cheap way to capture a right-to-left or Russian window (swap the single root token instead of a JSON
  round trip) silently edits a channel if it takes the first match. Related trap from the same run:
  `[regex]::Match` on an absent token returns a **zero-length match at index 0**, so a
  `Remove/Insert` at `$m.Index` prepends the replacement to the document and the state file stops
  parsing altogether (2026-08-07).

- **Three ticket numbers were allocated twice.** `SP-0054` was taken by the verified clock-jitter ticket
  *and* by the rubric draft (renumbered to `SP-0061` on 2026-08-07; the clock-jitter one is cited from
  shipped code, so it keeps the number). `SP-0053` was taken by "About this channel" *and* by the
  snapshot-freshness draft (renumbered to `SP-0066` on 2026-08-08, same tie-break: About is cited from
  shipped code in twenty-five places, the draft in two lines of tooling prose). `SP-0056` was taken by
  the verified `visible_download_progress` *and* by `catalog_list_costs_what_is_visible` (renumbered to
  `SP-0067` on 2026-08-08, same tie-break: the download-progress work is cited from shipped code in
  fourteen places, the performance ticket in none - it had no code yet). Re-scan `PLAN/` and
  `PLAN/DONE/` immediately before writing a new ticket file; a scan from earlier in the session is what
  produces this.

- **A single-run millisecond figure on this machine is not evidence.** Three identical scripted sessions
  against the same 19 855-channel catalog, same binary, gave `ApplyFilter` medians of 96.3, 154.0 and
  96.9 ms and browsing-session-save medians of 63.2, 183.2 and 85.5 - up to 3x apart. Counts, the
  `scanned=` bounds and written byte counts were stable to the digit across all three. Measure the
  quantity the change actually controls; treat ms as a coarse "nothing got dramatically worse" check.
  Corollary that made SP-0067's criterion 1 provable: record the *request* as well as the *evaluation*
  (`op=FilterRequested` before the debounce, `op=ApplyFilter` after), so one run's log shows the collapse
  without needing a differently-built binary to compare against (2026-08-08).

- **`CatalogState` reference equality is a usable cache key, with one exception worth knowing.** Every
  change to the channel list's *membership* - add, import, hide/delete, purge, refresh - goes through
  `_state with { Channels = ... }` and yields a new instance. `ReplaceChannel` is the sole in-place
  mutator and only ever swaps one element for another, never adds or removes; `StreamCatalogStore.SaveAsync`
  also returns the *same* instance when no atlas is replaced. So `ReferenceEquals(_cachedSource, _state)`
  correctly gates anything that depends on membership, and does not gate anything that depends on a single
  channel's fields (2026-08-08, SP-0067).

- **`artwork-manifest.json` does not cover `stream-catalog.zip`.** Its `sets` are exactly two -
  `channelPreview` and `streamLogo`, the tile packs - and the publisher's catalog path emits no stamp,
  hash or size record at all. Both `SP-0053`'s draft and `SP-0052`'s 2026-08-07 update consequence 4
  asserted the opposite ("a `stamp` per payload"), and that wrong premise survived into two ticket
  bodies and two lines of shipped tooling prose before anyone read the upstream schema
  (`delivery/stream-catalog/README.md`, the `artwork-manifest.json` section) or its producer. The
  catalog's only published freshness signal is the asset's HTTP `Last-Modified`, which the snapshot
  generator already stores verbatim as `snapshot.json` `sourceDate` - so the comparison is a `HEAD`
  against the `CatalogUrl` the app already knows, and it never needed the second network address that
  `SP-0052` decision 10 deferred the work over. Corrected in `SP-0066` (2026-08-08). Note the manifest
  *is* the right file for the preview-artwork payload, and that gap is now closed: SP-0091 (2026-08-20)
  moved the app off the pinned sheet revision onto the stable names plus `artwork-manifest.json`.

- **The test project can read the App's own source as data, and now does.** `Localization.*.xaml` had been
  linked in as `Content` since SP-0034 because tests depend on Core only; SP-0057 extended that to
  `src/StreamsPlayer.App/*.cs` and `*.xaml` so a gate could compare the shipped strings against the code
  that formats them. Two facts worth keeping. (1) The glob is deliberately **non-recursive** - `**` sweeps
  in `bin` and `obj`, whose generated sources are not call sites and whose file names collide. (2) The
  reader masks rather than parses: one pass blanks the body of every comment and literal while preserving
  length and line breaks, so bracket matching and comma splitting cannot trip on a comma inside a string, a
  `//` inside a URL, or a brace inside an interpolation hole. The case that breaks a naive scanner is
  `$"{map["k"]}"` - the nested literal ends the outer string early and every bracket after it is counted
  wrong. Measured coverage when written: 67 files, 214 literal-key call sites, 181 distinct keys.
- **`string.Format` is asymmetric, and that asymmetry used to be fatal here.** Surplus arguments are
  ignored in silence; a template referencing an index the caller did not supply throws. Every localized
  string is rendered from an `async void` handler and `App_DispatcherUnhandledException` logs without
  setting `e.Handled`, so adding a placeholder to a shipped string without finding its call site ended the
  process - the same failure shape as the state-save incident above. `LocalizationParityTests` forced the
  new `{1}` into all thirteen languages and still could not see the one-argument call site, which is what
  made this invisible. Since SP-0057 all rendering goes through `LocalizedFormat.Apply` (Core), which pads
  a short argument array with nulls and catches the unparseable template, and `LocalizedCallSiteTests`
  fails the build on the disagreement. Consequence for future work: a placeholder added to a string now
  *forces* its call site, and the surplus direction fails too - it is the signature of a placeholder
  deleted from the string and left behind in the code, which nothing at runtime can report (2026-08-08).

- **A generated artifact that only the owner's tree carries is invisible until a user reports the
  absence.** `src/StreamsPlayer.Core/Resources/catalog-snapshot.zip` was generated by SP-0052 and never
  committed. The build embedded it under an `Exists` condition, so every clone, every CI run and every
  release build succeeded and shipped an application whose first-launch offer, post-failure recovery
  offer and settings action all silently had nothing to apply - the only visible trace was one disabled
  button in Settings. Nothing was wrong with the code; the artifact was simply absent from git while
  the generator's help, its `-Check` mode and the release checklist all said "tracked" and "commit it".
  Since SP-0060 the artifact is tracked (`*.zip binary` in `.gitattributes`, so no content sniffing
  decides an archive's line endings) and a build without it fails with error `SP0060`. **The
  chicken-and-egg to remember:** `tools/build-catalog-snapshot.ps1` reads its contract from a *built*
  `StreamsPlayer.Core`, so a tree that has lost the artifact must build once with
  `-p:AllowMissingCatalogSnapshot=true` before it can regenerate. The general lesson: when a feature
  depends on a build-time payload, make its absence a build failure, because "condition on Exists" and
  "silently ship less" are the same line of MSBuild (2026-08-08).

- **A `PLAN/DONE/` folder is not evidence that its phases landed.** `SP-0042`'s INDEX claims phases 1-6
  shipped; the working tree contradicts it for at least four items, each re-found from scratch by
  SP-0069 on 2026-08-08: there was no `MediaEnded` handler anywhere in `src/` (phase 2's stated
  deliverable), no staged native teardown (phase 3), no in-session log size cap (phase 5), and
  `_sessionCts` was never disposed (phase 6's AC 8 - though that one turns out to be a *deliberate*
  omission recorded in `temp/leak-audit/DOSSIER.md` L6a, which the INDEX also fails to say). This is the
  same shape as SP-0060's untracked snapshot: a claim in a document outliving the artifact it describes.
  The rule that follows is cheap - before treating a closed ticket's work as present, `rg` for one symbol
  it must have created. Two greps would have saved most of an audit.

- **The GUI sandbox's dangerous failure is not "the run fails", it is "the run succeeds against the
  owner's real folder".** `Enter-SpSandbox` renames `%LOCALAPPDATA%\StreamsPlayer` aside, and both legs
  are fragile in ways that only show under load (SP-0069, 2026-08-08). (1) A live app instance holds
  `Current.log`, so the rename fails with `Access to the path is denied` - and a script that does not
  check will happily continue unsandboxed. (2) `Exit-SpSandbox` deletes the sandbox folder *before*
  renaming the backup back, so a still-running app makes the delete throw and the owner's catalog is left
  sitting in `StreamsPlayer.agentbak` - which happened, and was recovered by hand. Always stop strays
  before entering, stop the app before exiting, retry the restore, and hash `catalog-state.json` before
  and after. (3) Worse for planning: after a crashed run the folder stayed **un-renameable for the rest of
  the session with no owning process** - only the recently written logs were locked, the 10.8 MB state
  file was free, and 180 s of retries plus a graceful close did not release it. Signature of an on-access
  scanner or the indexer. Budget for the possibility that a measurement simply cannot run until later,
  and do not "work around" it by moving the owner's catalog file by file.

- **`MediaElement` reports a cleanly closed audio stream as `MediaEnded`, never `MediaFailed`** - and for
  five months nothing listened, so the session never ended: `_playingAudio` stayed set, the `WakeGuard`
  hold kept **forbidding the machine to sleep**, the sleep ticker kept counting and the SMTC session kept
  saying Playing. The fix is not a hard stop: the product already routes exactly this event through the
  bounded recovery policy on the video side (`PlayerWindow.Backend_EndReached` sends
  `PlaybackFailureSignal("end_reached", EndReached: true)`), and Core has carried the whole mechanism all
  along - `PlaybackRecoveryClassifier` maps that flag to `RecoveryTrigger.StreamEnded`. Audio simply never
  fed it. For radio that is also the right reading: a server closing the response is more often a relay
  dropping than a broadcast finishing, and `MediaElement` cannot tell them apart, so the budget reconnects
  a few times and *then* hard-fails into the funnel that releases the hold (SP-0069, 2026-08-08).

- **`ForgetRow` and `PruneRowCache` are a pair, and only one of them knew it.** Rows live in two maps -
  `_rowCache` by id and `_rowsByUrl` by URL - and dropping from the first without `UnindexUrl`ing the
  second strands a `ChannelRow` that nothing can ever reach again, because the prune walks `_rowCache`.
  The subtle part is the growth rate: hiding a channel again after unhiding it builds a *new* row and
  `IndexUrl` appends it under a reference-equality check, so the leak is one row per **gesture**, not one
  per channel. Any future second index over rows needs the same pairing (SP-0069, 2026-08-08).

- **A rule that costs a re-open is priced in black screen, not in events - and the agent's own runs will
  not show it.** SP-0071's probe looked correct on every agent run and on 33 unit tests; the owner's first
  real session was the measurement that mattered: `legs=10 | reconnects=0 | stalls=11` meant *every*
  interruption was the feature's own re-open, 108.9 s of black out of 656 s, 73 % of it spent probing.
  The number to compute from a session log is `sum(ttff_ms) / session_ms`, and `legs - reconnects` says
  how many of those interruptions the feature caused itself. Two defects followed from the same root -
  state whose lifetime was one scope too narrow: the probe wait belonged to the governor rather than the
  rung (a success at 796k forgave a top rung already failed three times), and the whole record belonged to
  the window rather than to the source (every re-open of the channel re-learned it, 60 % of the remaining
  black screen). Both were invisible to tests that only ever exercised one governor for one session
  (2026-08-08).

- **libvlc surfaces ICY now-playing over `http://` and not over `https://`, and its `Title` field is the
  URL's last path segment.** Measured on the same station in the same minute (SP-0073, 2026-08-08):
  `http://ice1.somafm.com/groovesalad-128-mp3` reported `NowPlaying = Bistro Boy - Journey` at 2.0 s,
  while the `https://` form of the *same* mount stayed blank for the whole watch - the station itself was
  fine, a raw request with `Icy-MetaData: 1` returned `icy-metaint: 45000` and
  `StreamTitle='Bistro Boy - Journey'`. So a TLS station will never show a track in the player, and that
  is VLC's HTTPS access module, not our code. The second half matters as much: `MetadataType.Title` came
  back as `groovesalad-128-mp3` and `master.m3u8` - the URL's tail - so using it as a fallback for
  `NowPlaying`, which is the obvious design, would print a link under the channel name. Consult
  `NowPlaying` and nothing else. Also confirmed: libvlc *does* refresh the field mid-stream (three
  distinct values at 2/22/45 s against a proxy injecting a change every 20 s), so a session that shows one
  value for three minutes is a station that has not changed track, not a caching bug - that
  misreading cost a run.
- **FlyleafLib reads a stream's metadata once and never again.** `Demuxer.Metadata` is filled in
  `FillInfo()` during `Open()`; nothing in the demux loop re-reads `fmtCtx->metadata` and the library
  never checks FFmpeg's `AVFMT_EVENT_FLAG_METADATA_UPDATED`. Polling that property therefore reports
  whatever the stream said at open, for the life of the leg - which looks like a working feature on a
  station whose title happens to be set at connect. Parity with the LibVLC engine needs FFmpeg's own live
  `icy_metadata_packet` AVOption off `FormatContext->pb`, read under the library's public `lockFmtCtx`;
  that is why `StreamsPlayer.App` now sets `AllowUnsafeBlocks` for exactly one member
  (`FlyleafVideoBackend.ReadNowPlaying`). Keep it to that one (SP-0073, 2026-08-08).
- **FlyleafLib's public `Player.Speed` re-buffers on every assignment, so no rate-control loop can be
  built on it - the library's own `Config.Player.MaxLatency` is the only non-stuttering path.** The setter
  sets `requiresBuffering = true` and `RequiresResync = true`, which is a visible stall per nudge; the
  private `ChangeSpeedWithoutBuffering` that avoids it is reachable only by setting a non-zero
  `MaxLatency`, after which the video screamer calls `CheckLatency()` once per presented frame. Three
  consequences that decide any live-latency design on this engine (read out of 3.10.4 by decompiling the
  referenced assembly, SP-0078, 2026-08-08): the correction speed is
  `max(round(distance / MaxLatency, 1, ToPositiveInfinity), 1.1)` - **the 1.1x floor is hard-coded**, so a
  Media3-style 1.02x nudge is not available and a corridor can only make the correction rarer, never
  gentler; the distance is measured as the *client's own undisplayed queue*, not a broadcaster offset, so
  it caps at `Demuxer.BufferDuration` and a buffer at the target makes the rule inert; and above 4.0x the
  engine discards the queue instead of playing it out, so the buffer-to-target ratio is what separates a
  gradual catch-up from a visible jump. Setting `MaxLatency` also raises `Demuxer.BufferDuration` to twice
  the target - overriding a smaller per-play buffer - and forces `Decoder.LowDelay`; setting it back to 0
  restores both. Audio at a non-unit speed goes through FFmpeg `atempo`, so pitch survives, and that path
  is live only because `avfilter-11.dll` is already in `FFmpegComponents.RequiredLibraries`. Read
  `MainDemuxer?.IsLive`, never `Player.IsLive`, whose getter dereferences a null demuxer.
- **`HttpClient` cannot talk to a Shoutcast v1 station at all**, and this is the measured cause behind
  SP-0074's "the string almost never appears". A v1 server greets with `ICY 200 OK` instead of
  `HTTP/1.1 200 OK`, and .NET throws `HttpRequestException: Received an invalid status line: 'ICY 200 OK'`
  before a single header is read. Proved against a fake station answering each way in turn, same code
  path, same socket: the `HTTP/1.1` case read `icy-metaint` fine, the `ICY` case threw
  (`temp/SP-0074/probe-icy-status-line.ps1`). `IcyMetadataReader` swallows it in a bare `catch`, so the
  station is indistinguishable from one that sends no metadata - which is exactly the invisibility that
  ticket exists to end. Fixed in SP-0074 by a plaintext socket fallback entered on
  `HttpRequestException.HttpRequestError == InvalidResponse` (the typed signal - matching the English
  message text would break on a localized runtime); the frame pump is reused unchanged, because the
  greeting was the only thing that ever differed (2026-08-08).
- **"Cancelled" was about to make SP-0074's log useless, and only running it showed that.** A live
  station's metadata read is almost always torn down by the user moving on, not ended by the station, so
  the first implementation logged `outcome=Cancelled` for a station happily feeding titles *and* for one
  that connected and never said a word - the precise distinction the ticket was written to obtain. The
  fix is a sink that remembers whether a real title went through, so the cancelled path reports
  `TitlesReported` when one did. Nothing in the test suite could have caught this: every case was green
  and the enum was fully covered. The general shape - an outcome enum whose most common value is the
  least informative one - is worth checking for whenever a long-lived read reports how it ended
  (2026-08-08).
- **A metadata feature cannot be observed on a public URL, and the reason is structural.** The player only
  opens URLs whose path carries a video extension (`StreamMediaKindClassifier`), while every station that
  actually announces a track is a plain ICY endpoint with no extension at all; SomaFM publishes no HLS
  (its own `channels.json` lists only `.pls`), and sampled HLS sources announced nothing. The way through
  is `temp/SP-0073/icy-proxy.ps1`: it serves one real station under `/gs.ts` so the app routes it to the
  player, under `/silent.ts` with ICY simply not requested (a control that changes the metadata and
  nothing else), and with `-Inject` rewrites each ICY block on a fixed cadence so "does the line follow a
  change" is a bounded observation instead of a wait on four-to-seven-minute tracks. One trap cost three
  debugging rounds: **forward the upstream reply's headers verbatim.** VLC recognises an ICY source by the
  `icy-name`/Icecast signature and only *then* re-requests with `Icy-MetaData: 1`; a proxy that rewrites
  the response down to `Content-Type` + `icy-metaint` never triggers that, and the client instead gets
  metadata bytes it was never told to expect - which shows up as decoder errors and a silent title, not as
  a proxy bug (2026-08-08).
- **Writing an invisible character literally into a source file is a defect even when it works.** SP-0073's
  bidi-strip rule and its test both first went in with real U+202E/U+200F characters pasted into the C#,
  in the very file whose job is to neutralize them: unreviewable in a diff, and one re-encoding away from
  silently ceasing to match. Both are now named `(char)0x202E`-style constants and the rule file is pure
  ASCII, checked by a byte scan rather than by eye. Note the related trap: `char.IsControl` does **not**
  cover these - they are format characters - so the pre-existing ICY sanitizer had been letting them
  through into the radio line since SP-0014 (2026-08-08).

- **A modal dialog owned by a hidden window is invisible, not merely awkward - and the always-on-top
  sibling is what makes it fatal.** SP-0080 collapses the catalog by `Hide()`ing it and showing a
  topmost panel, and `MainWindow` raises two modals owned by `this`:
  `FailAudioTerminallyAsync`'s `PlaybackFailureDialog` and `PlayChannelAsync`'s offline `MessageBox`.
  Owned by a hidden window, both render *below* the topmost panel with no taskbar button of their own,
  so the listener gets a frozen application and no way to answer. Reachable in normal use - a station
  whose reconnect budget runs out while the catalog is collapsed is exactly the case the panel exists
  for. Neither "expand to show it" nor "re-own it to the panel" is right here, because the ticket had
  already ruled that trade: a window jumping over someone's full-screen work is worse than a quiet
  line. Both now take the status-line route SP-0062's resume path has taken since it shipped.
  **The general rule: before hiding a window, enumerate every modal that names it as `Owner`.** In
  this repo `rg "Owner = this"` finds them, and there are eleven (2026-08-19).
- **`LocationChanged` is the wrong place to clamp a window's position, and WPF exposes no right one.**
  Writing `Left`/`Top` while the modal move loop still owns the window makes it fight the cursor - the
  listener sees jitter, not a limit. The signal that says the drag is over is the Win32
  `WM_EXITSIZEMOVE` (`0x0232`), which has no WPF event; `CompactPanelWindow` hooks it through
  `OnSourceInitialized` + `HwndSource.AddHook` and raises its own `MoveFinished`. Two adjacent facts
  from the same work: `MonitorFromRect(MONITOR_DEFAULTTONEAREST)` + `GetMonitorInfo` is the whole
  answer to "the monitor it stood on was switched off" - no `System.Windows.Forms` reference needed,
  which this project does not carry - and the device/DIP transform must come from the window being
  *placed*, not from whichever window is doing the placing, or a mixed-scaling desktop lands it off by
  the DPI ratio. Measured on a five-monitor desktop: dragged to `8815,4835`, the panel came to rest at
  right edge `8820` / bottom `4839`, exactly `\.\DISPLAY6`'s work area (SP-0080, 2026-08-19).
- **A window shown while its sibling is hidden gets the taskbar button, and that is how "one
  application, two views" is built.** `ShowInTaskbar` decides `WS_EX_APPWINDOW`, and Alt+Tab follows
  the same rule, so the check that actually proves the criterion is an `EnumWindows` pass counting
  windows where `visible && !WS_EX_TOOLWINDOW && (WS_EX_APPWINDOW || no owner)` - one, while
  collapsed. Do **not** reach for an owned window here: `PlayerWindow` clears its `Owner` in `Loaded`
  for the opposite need (independent minimising), and an owned window carrying its own taskbar button
  is the shape that produces two entries. SP-0080 routes the panel's close through `MainWindow.Close()` so
  the ordinary save path runs (2026-08-19). Since SP-0120 (2026-09-25) `ShutdownMode` is
  `OnExplicitShutdown`: closing the catalog is the only ordinary way the process ends, so a new top-level
  window must never be the thing that is expected to end it.
- **The stream bank is republished in place several times a day, so any row count written into a
  ticket is stale before the ticket is finished - and the atlas can stay byte-identical while the CSV
  changes underneath it.** Measured inside a single session on 2026-08-19: at 18:45 the asset was
  7 557 268 bytes with 19 534 rows and 5 624 favicon indices; at 21:56 the *same URL* served
  7 487 265 bytes with 17 628 rows and 5 252 indices - 1 906 channels withdrawn in about three hours -
  while `favicon-atlas.png` was byte-for-byte identical (same SHA-256, same 512x11488). Two
  consequences. First, a refresh that "loses" thousands of rows is not a merge bug: verify against a
  **freshly downloaded** bank before investigating the code, because a snapshot taken earlier in the
  same session is already a different artifact. Second, an unchanged atlas size or hash proves nothing
  about the CSV, so the pair must always be taken from one download - which is exactly what
  `StreamCatalogService` does by committing rows and atlas in one `SaveAsync`. Cite counts with the
  timestamp of the download they came from, never as standing facts (SP-0087, 2026-08-19).
- **A pinned upstream asset revision is a trap the pin cannot detect, and our code comment argued the
  opposite.** `ChannelPreviewAtlasService.Revision` pins `-v3` and its comment reasons that the pin is
  *protective*: "the publisher ships a tile-incompatible rebuild under a new suffix so an older client
  keeps resolving the sheet it was built against". The publisher's contract says the opposite of the
  premise - revisioned artwork names are **frozen artifacts**: never deleted, and never rebuilt again.
  So the pin does not hold a compatible payload, it holds a *dead* one, and it looks healthy forever
  because the asset it names keeps returning 200 with the last bytes it ever had. Measured 2026-08-20:
  `channel-preview-atlas-v3.webp` was still the 60-row `8160x8100` sheet from 2026-08-12 while the
  contract described a current `8160x11340` build. The current revision is a fact about today published
  in the producer's README, never a constant - read artwork through `artwork-manifest.json` and the
  stable names instead (SP-0091). General form worth carrying beyond this asset: when a comment
  justifies a hardcoded upstream version by asserting what the publisher will do, verify that assertion
  against the publisher's own contract - a wrong one is unfalsifiable from inside the client.
  **Confirmed and made worse the same day.** Between the morning measurement and the SP-0091 build,
  `channel-preview-atlas-v3.webp` was *rebuilt in place*: same name, 09:38, `8160x10935` and 2 723 tiles
  where hours earlier it had been the 60-row `8160x8100` from 2026-08-12. The producer's own "frozen,
  never rebuilt again" promise about revisioned names did not hold - so a pinned name is not merely a
  way to go stale, it is a payload that can change **shape** underneath a client that believes its
  geometry is settled. Two lessons kept: read a revisioned name as no promise at all, and never
  hardcode a row count against any sheet (we never did - `IsInBounds` measured the decoded image, which
  is the only reason this was survivable). SP-0091 removed the sheet path outright.
- **The published artwork pair can tear, and only the manifest can catch it.** Publishing is
  delete-then-upload per asset, so `channel-preview-coords.json` and `channel-preview-tiles.zip` are
  replaced separately: a rebuild landing between our two fetches gives two files that each answer 200,
  each parse cleanly, and whose index spaces disagree. The result is not a missing picture - it is
  another station's still on a channel that looks perfectly healthy, the failure shape of source
  contract item A. Nothing downstream can detect it, because a seeded JPEG in `grid-previews/` is never
  re-checked against anything. `artwork-manifest.json` declares `size` + `sha256` per file and a per-set
  `stamp` (which, as published on 2026-08-20, is simply the tile pack's own hash), and SP-0091 verifies
  both files against it before a single frame is written. If a future change makes the artwork fetch
  cheaper by skipping a hash, that is the thing it is skipping.
- **The winget-pkgs bot names a cause; the check runs hold the verdict, and on 2026-08-26 they
  disagreed.** #422124 came back twice with `msftbot/validationError/installers/validationDefender` -
  a Defender template naming the Installers Scan test - plus the `Validation-Defender-Error` label.
  `gh api repos/microsoft/winget-pkgs/commits/<head-sha>/check-runs` said `07. Installers Scan`
  **passed** and `08. Installation Validation` failed, having run exactly 17:27:44Z -> 19:27:44Z with a
  progress log holding only `Status: Waiting`, `Status: Completed`, `Error: Failed`. A round two-hour
  span with no installation progress is the sandbox window expiring; the real cause was payload weight
  (140 MB compressed, 322 MB and 920 files extracted), and the canon's winget reference already
  documented that abort. **Two things generalize.** The comment template flattens a per-step failure
  into one wrong sentence, so read `check-runs` and its `output.text`, never the comment, and never
  chase the label the comment brings with it. And when an external gate blames the artifact, re-verify
  the artifact independently before believing it - here the hash matched the manifest exactly and a
  local `MpCmdRun -Scan -ScanType 3` at the current signature found nothing in either asset, which is
  what made it safe to stop looking for malware and start looking at the clock. Caveat on that scan:
  `Get-MpPreference` showed `MAPSReporting=0` on this machine, so it is signature-only and cannot
  reproduce a cloud `!ml` verdict - a clean local scan is evidence, not proof.
- **A winget-pkgs manifest edited in a clone lies about its own line endings.** Upstream
  `.gitattributes` marks them `text=auto`, and git reads it from the index even under a sparse
  checkout, so a checked-out file shows CRLF while the stored blob is LF. Inspecting the bytes on disk
  therefore proves nothing, and `core.autocrlf false` does not override the attribute. Verify the blob:
  `git cat-file -s HEAD:<path>` against the on-disk size shows how many CR bytes were dropped. The
  merged manifests are UTF-8 **with BOM** and **LF** - confirmed 2026-08-26 by fetching 26.0809.0022,
  26.0819.0156 and 26.0820.1828 raw from the contents API, all three 671 bytes, BOM present, zero CRLF.

- **An agent can drive the WPF grid itself for a rung-7 observation, but only by posting the message.**
  `SetForegroundWindow` + `mouse_event(MOUSEEVENTF_WHEEL)` from a background PowerShell does nothing
  useful - Windows refuses the focus change, the wheel lands on whatever is focused, and the log shows
  only `PREVIEW COORD state=stopping/started` from the activation churn. `PostMessage(hwnd,
  WM_MOUSEWHEEL, delta << 16, (y << 16) | x)` with the cursor parked over the list works without focus
  and produces real `ScrollChanged` events. Two traps beside it: `CloseMainWindow()` on a process whose
  `MainWindowHandle` is a **player** window closes only that window and leaves the catalog running - and
  that instance keeps holding `Current.log`, so the next launch takes the SP-0085 reserve path and the
  evidence is in `Session-<stamp>.log`, not in the file you are tailing (2026-09-19).

- **Never rewrite a `Localization.*.xaml` with Git Bash `awk`/`sed`.** They run in text mode and drop
  the CR of every CRLF, and eleven of the thirteen dictionaries also carry three committed `\r\r\n` lines
  (`RandomStationTip`, `PrevStationTip`, `FrameFolderReset`). Those lone CRs are what keep git from
  normalizing the file; lose them and `git diff` reports the whole file (591/578) for a one-line change.
  Use the Edit tool, or PowerShell `ReadAllBytes`/`WriteAllBytes`, and check `git diff --numstat` after
  (2026-09-24, SP-0114). **Recurred the same day in SP-0107** - a 13-locale insert done with `awk`
  because Edit is one file per call. Read this entry before any bulk dictionary edit. Two more traps
  found fixing it: `$'\r'` inside a Bash tool command does not reach `grep` as a CR, so CR counts made
  that way are fiction - count in PowerShell (`[regex]::Matches($text, "\r\n")`); and the repair is to
  diff against the HEAD blob exported with `git show HEAD:<path> > file`, then restore both the CRLF
  and the three `\r\r\n` keys with a PowerShell `[IO.File]` read/replace/write (UTF-8, no BOM).
  Two more from SP-0160 (2026-09-30): a PowerShell quoted string cannot carry the typographic
  apostrophe U+2019 (or U+2018/U+201C/U+201D) - the tokenizer treats them as string delimiters, so a
  script carrying `прев’ю` fails to parse; build such text as `.. + [char]0x2019 + ..`. And endings
  are a fact about the checkout, not about the file: on this tree the dictionaries sat LF with zero
  lone CRs while this entry said CRLF - count CRLF/LF/lone-CR before inserting and match what is
  there (a first insert cost one normalize-back pass; `git diff --numstat` 1/0 per file is the check).
- **UI Automation on this app, three traps** (2026-09-24, SP-0114): owned windows (History, Settings, a
  MessageBox, a context menu) are top-level, so search the process's top-level windows, not the main
  window's descendants; a Settings control on an unselected tab is not in the tree until the tab is
  selected; and a label and its combo box share the name, so filter by `ControlType`. A WPF
  `MessageBox`'s OK is a `Pane` without `InvokePattern` - dismiss it with Enter.
  Three more (2026-09-25, SP-0124): a catalog card is a `DataItem` with **no UIA children**, so its Play
  and More buttons cannot be reached by automation at all; an agent session may have **no input desktop**
  (`GetCursorPos` reads 0,0, synthetic clicks go nowhere, `PrintWindow` returns black), so do not plan on
  mouse input; and `AutomationElement.RootElement` children by process id can miss an owned modal that
  `EnumWindows` sees. What works without input: launch with `--id <guid>` (startup) and run a second copy
  with `--id` (SP-0118 forwards it into the running one) - both enter the same `PlayChannelAsync` a click
  does; find windows with `EnumWindows`, read and press buttons through `AutomationElement.FromHandle`.
  `temp/SP-0124/observe.ps1` is a working example, including the profile sandbox.
  Two more (2026-09-26, SP-0132): **`InvokePattern.Invoke` ignores the Win32 disable a modal puts on its
  owner**, so automation can press a catalog button - collapse, refresh - underneath an open `MessageBox`,
  a state no user can reach; a run that "reproduces" a bug that way proves nothing. Order the steps so the
  press happens before the modal can appear, and check the log timestamps. And the input desktop is not
  always absent: that session had one (`OpenInputDesktop` non-zero, `SetCursorPos` moved the real cursor),
  which made a card's Pin reachable by a computed mouse click; restore the owner's cursor afterwards. A
  `MessageBox`'s Yes/No are `Pane`s too - `SetForegroundWindow` on the `#32770` and send `n`. For a
  performance claim, the deployed build in `C:\GD\i` against the same sandbox is a ready "before".
- **A frozen `BitmapDecoder` frame is still thread-bound; decode off-thread with a frozen `BitmapImage`.**
  Found 2026-09-25 (SP-0125): `BitmapDecoder.Create(..., OnLoad).Frames[0]` decoded in `Task.Run` and
  `Freeze()`d threw "a different thread owns it" on every `CroppedBitmap` taken on the UI thread; the
  `Image` binding swallowed it, so every favicon silently became a monogram while build and tests stayed
  green. `BitmapImage` + `CacheOption = OnLoad` + `Freeze()` owns its pixels and works. Only a GUI probe
  of a row that *has* an index catches this - a screen of top rows proves nothing, 70% have no icon.
  `temp/SP-0125/run-refresh-memory.ps1` searches such a row by exact title.
- **The contract store's registry is rewritten by peer sessions while a sync runs, and two split traps
  (2026-09-26, the `CAPTURE-OUTPUT` sync).** `_meta/REGISTRY.md` in the store changed twice under this
  session inside twenty minutes - CRLF became LF, 29 lines arrived, and the row chosen as an insertion
  anchor was reworded - so an insert must find the section (`## 2. `, `## 3. `) and the last matching
  table row at run time, never a line number or a remembered row text, and re-check that this product's
  rows are still absent immediately before writing. Two tool traps, each one round: PowerShell 7
  `-split "pattern", -1` splits from the *end* - a negative limit is right-to-left, so it returned one
  piece - use `[regex]::Split`; and in the Bash tool `$'\r'` inside `$( )` inside double quotes is a
  parse error for the whole call - count CRs in PowerShell.


- **A site audit that needs real input, reduced motion or a 360 px viewport is run over the DevTools protocol, and five things falsify a naive run (2026-10-07, SP-0199).** The Chrome-extension automation could not give a page focus (`document.hasFocus()` false, Tab a no-op); a separate `chrome.exe --headless=new --remote-debugging-port` driven from Node (global `WebSocket`, no packages) gets trusted keys (`Input.dispatchKeyEvent` `rawKeyDown` + `keyUp`, `windowsVirtualKeyCode` 9), `Emulation.setEmulatedMedia` and a coarse-pointer phone (`setDeviceMetricsOverride` with `mobile: true` plus touch emulation). The traps: headless Chrome here reports `prefers-reduced-motion: reduce` by default (the OS animation setting), so a "baseline" must send `no-preference` explicitly; `Page.captureScreenshot` clips are in document coordinates; `html { scroll-behavior: smooth }` means a rect read right after a Tab is mid-scroll, so wait for `scrollY` to settle; `docs/404.html` addresses its CSS and scripts from the published base URL, so a local serve silently reads the *published* copy unless `Fetch.enable` remaps that base URL (`tools/site/build-site.ps1` holds it) to the local port - and a tracked file that spells the site address out fails the held-address gate; and never stretch the viewport to the document height to take one screenshot - `.privacy-main` has `min-height: calc(100vh - ..)`, the page grows with the viewport, the footer lands outside the image and the clamped edge pixels read as contrast failures (scroll an ordinary 1428 x 839 viewport in steps and sample the pixels under each text line after hiding the text instead). Computed-colour contrast (SP-0197) had missed a 0.85 `opacity` and an icon-only link 30 px wide; pixel sampling and a real coarse-pointer viewport found both. The harness is `temp/SP-0199/` (`cdp.mjs`, `tab.mjs`, `motion.mjs`, `width.mjs`, `contrast2.mjs`, `hover.mjs`, `locales.mjs`, `origins.mjs`, `overlap.mjs`); `temp/` is untracked, so promote it to `tools/site/` before the next release that changes the site's chrome if the run is to be repeated.
