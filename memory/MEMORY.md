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
  **It happened again on 2026-08-09**, PR #414229, in the same two ways - a self-invented body with
  its own `### Validation` heading, and the `New version:` title.
  **Correction, 2026-08-21: `New-Manifest` was never the tell, and this entry taught that wrongly
  twice.** Checked against the API: every merged submission this package has - #414229, #420274,
  #421532 - carries `New-Manifest` alongside `Validation-Completed`, `Moderator-Approved` and
  `Publish-Pipeline-Succeeded`, and #420274 and #421532 both went out with the *correct* `Update:`
  title. The bot applies it because the pull request adds a manifest folder, which every new version
  does. So the label is noise, the title rule stands on its own merits, and **a symptom that appears
  on the successes too is not evidence of the failure** - which is the general lesson worth more than
  the winget detail. Confirm a diagnosis against the cases that went right before believing it.
  Why the entry did not prevent it, which is the part worth keeping: this file was never opened that
  session. It was grepped for one line and appended to, and the top was never read, so an entry
  sitting at line 12 changed nothing. **Two conclusions.** Read this file before acting, not while
  editing it. And a rule that guards one step belongs at that step as well as here - the winget rule
  now also lives in `winget/README.md` beside "submit a pull request", which is the document actually
  open during a release. Treat a repeat of a recorded correction as evidence the rule is in the wrong
  place, not only as a lapse.
  Also: the template is not stable. On 2026-08-09 it had emoji headings (`## 📖 Description`,
  `## ✅ Checklist`, `## 📦 Manifest Checklist`) and a `This PR only modifies one (1) manifest` box
  that did not exist in July. Fetch it every time; never reproduce a remembered copy.

## Project

- **The FFmpeg natives FlyleafLib publishes are GPLv3, so they can never be bundled.** The `FFmpeg`
  folder inside `Flyleaf_v3.10.4.7z` is built `--enable-gpl --enable-version3` with libx264/libx265;
  its `avutil` reports `GPL version 3 or later`. FlyleafLib *itself* is LGPL-3.0, which is what made
  SP-0026's original "ship both native stacks" decision look safe - the licence trap is one layer
  down, in the binaries upstream tells you to fetch. StreamsPlayer therefore downloads an **LGPL**
  build instead (`BtbN/FFmpeg-Builds`, `ffmpeg-n8.1-latest-win64-lgpl-shared`), on explicit user
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
  is the latch (set as the first statement of `Closed`, before any await) and `GridPreviewCoordinator`
  carries its own `_disposed` flag because it owns the semaphores that a late `StartAsync`/`StopAsync`
  would wait on. Closing the catalog also closes every open `PlayerWindow` - explicitly, through
  `MainWindow.CloseOpenPlayerWindows()`, since a player is a top-level window and no longer WPF-owned by
  the catalog - and their `Closed` handler calls back into `StartPreviewsAsync`, so the late caller is
  not always an input event.
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
  is the shape that produces two entries. Also note the default `ShutdownMode` is `OnLastWindowClose`
  and a hidden window still counts as open, so closing the visible one does **not** end the process -
  SP-0080 routes the panel's close through `MainWindow.Close()` so the ordinary save path runs
  (2026-08-19).
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
  ("Add a channel someone sent you as text..."), while the overflow entries carry their header text.
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
  set lives at `P:\ANDROID\FastMediaSorter_mob_v2\dev\handoff\streams-source-spec\` - files `01`, `03`,
  `04` and `09` define the bank, the CSV, the favicon atlas and the artwork atlases, and a dated
  `NN_contract_amendment_*.md` **amends** them, so read the highest-numbered amendment first and treat
  the rest as the rules it edits. Its own words: "Where a consumer's current behaviour differs from a
  rule below, the consumer changes - not the rule." Nothing in `StreamsPlayer` mirrors these files, and
  `docs/specifications/` is unrelated - so when the owner says "the spec set", do not search this
  repository and do not infer the contract from our own code comments. The delivery artifacts themselves
  live on one GitHub release, tag `delivery-so-v1` of `SerZhyAle/FastMediaSorter_mob_v2`; the release
  asset list and `artwork-manifest.json` are the cheapest way to tell what is actually published from
  what a document says is published, and on 2026-08-20 those two disagreed.

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
  because nothing in the repo was wrong. SP-0093 pins `Microsoft.WindowsDesktop.App` to 10.0.10 in
  `Directory.Build.targets`. **The published 26.0820.1828 shipped with 10.0.11 and is affected** - it was
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
  tag, wait for the release to land and verify the asset, then commit `docs/` and push. Nothing in CI
  gates `docs/` against `tools/site/templates`, so the split leaves no red build in between - the only
  cost is remembering to run `build-site.ps1` and make the second commit (2026-08-21).

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

- **`reference` - a leftover StreamsPlayer process silently swallows a `--url` launch.** The app is
  single-instance: starting it while another copy runs hands off, and the new process contributes
  nothing to `Current.log`. The symptom is a log full of ordinary catalog activity and **no `AUDIO`
  line at all** - identical to what a mis-typed invocation looks like, and easy to misread as the
  playback bug itself. `Get-Process StreamsPlayer` and kill before any run-and-observe check
  (2026-08-21).
