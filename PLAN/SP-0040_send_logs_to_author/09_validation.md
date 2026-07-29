# Phase 09 - Validation

**Status:** Planned

## Automatic gates

1. `./scripts/check.ps1` - Release restore + build + full test run. Expected: build 0 errors,
   all tests pass including the localization parity gate and the three new Core suites.

## Run-and-observe (a GUI action is not proven by a build)

Evidence goes to `temp/SP-0040/`, each item recorded as `expected: ... | actual: ...`.

2. **Retention (criteria 7, 8).** Launch, close, launch again; then inspect
   `%LOCALAPPDATA%\StreamsPlayer\`: `Previous.log` holds the first session's shutdown line and
   `Current.log` the second session's startup line.
3. **The archive (criteria 2, 3, 9).** Press the button; list the ZIP's entries; open
   `environment.txt`; confirm no `catalog-state.json`, no URL and no channel title in the summary;
   press the button a second time and confirm the reports folder still holds one archive.
4. **The mail and the reveal (criteria 4, 5).** Observe the composed message - recipient, subject
   with the version, body naming the archive - and the file-manager window with the archive
   selected. Capture both.
5. **The quality trace (criterion 12).** Play a genuinely unstable stream long enough to stall,
   on the default backend, then repeat for an audio station and once on the alternate backend.
   Then send the logs and read the archived log: the open, the go-live, the stalls, each recovery
   decision with its attempt and budget, and the closing session summary must all be present and
   consistent with what was seen on screen. This is the criterion the owner asked for by name -
   it is not satisfied by a build or by reading the code.
6. **Failure path (criterion 10).** With the reports folder made unwritable, confirm the localized
   explanation appears and the app stays usable.

## Exit

All sixteen criteria PASS, or the ticket goes to `BlockNeedUserTest` naming exactly which
observation is missing and why.

## Checks

- Status: Implemented, with one criterion left to the owner (see below).
- expected: release-parity gate green | actual: `./scripts/check.ps1` - Release build 0 warnings / 0 errors, `Total tests: 402, Passed: 402` (was 398 before this ticket; +5 log-file, +6 summary, +6 archive, +4 mail-link facts, minus none).

### Observations, all under `temp/SP-0040/`

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Button + tooltip + disclosure line in About | PASS observed | `about-tab.png` (Italian): `Invia i registri all'autore` and the five-line hint |
| 2 | Archive holds both logs + summary, nothing else | PASS observed | entries `Current.log` 6131, `Previous.log` 10516, `environment.txt` 392 - and nothing else |
| 3 | Summary carries no channel data | PASS observed | dumped `environment.txt`: counts only, no title, no URL |
| 4 | Prepared mail: recipient, subject, body | **BLOCKED - owner** | The shell did hand the link to the default handler and the new Outlook opened - on its account-setup screen, because it has never been configured on this machine (`after-send.png`). The link itself is proven by `DiagnosticMailLinkTests` (4 facts, including `&`, `#` and CRLF escaping), but nobody has seen the composed message |
| 5 | Archive selected in a file manager | PASS observed | Explorer window in `about-tab.png`: `1 item  1 item selected  2.85 KB` |
| 6 | Nothing leaves the device unless pressed | PASS | No HTTP client, no upload, no scheduler on the path; the only egress is the user's own mail program |
| 7 | The failed session survives a restart | PASS observed | launch/close/launch: `Previous.log` = first session (4399 B, original timestamp), `Current.log` = second |
| 8 | An unrotatable log does not cost the launch | PASS observed | `Previous.log` held open `FileShare.None`: app started, wrote a fresh 6111-byte `Current.log` with its startup line, previous kept its old content |
| 9 | No archive accumulation | PASS | test `Build_Twice_LeavesExactlyOneArchive`; every observed run left exactly one zip |
| 10 | Localized failure explanation | PASS observed | reports path replaced by a file: `archive-failure-rtl.png` shows the Arabic message box `تعذّر إنشاء أرشيف السجلات.`, log line `LOG REPORT ok=false err=IOException`, app stayed usable |
| 11 | Thirteen languages, automation names, mirrored layout | PASS observed | parity gate 25/25; `about-tab-rtl.png` shows the Arabic About tab fully mirrored with the new button and hint unclipped; automation name read back in Arabic |
| 12 | The log tells the whole story of a bad stream | PASS observed (one gap) | `trace-video-live.log`: watchdog freeze -> `PLAYBACK RECOVER attempt=1 budget=3` -> `PLAYBACK LIVE` -> `PLAYBACK SESSION outcome=live legs=2 reconnects=1`. `trace-fail.log`: four bounded reconnects with 2/4/8/16 s backoff -> `HardFail`. `trace-audio-real.log`: `AUDIO SESSION outcome=live ttff_ms=2520`. `trace-audio-broken.log`: `AUDIO SESSION outcome=failed legs=5 reconnects=4`. Gap: the `FLYLEAF *` records cannot run here (no FFmpeg natives -> factory falls back to LibVLC) |
| 13 | Summary names the backend and its detail level | PASS observed | `media_backend=LibVlc`, `backend_stats=detailed` |
| 14 | README x3 | PASS | feature bullet plus a corrected privacy paragraph in `README.md`, `README.ru.md`, `README.uk.md` |
| 15 | Site, every locale, generated | PASS | generator exit 0, 26 pages, stale privacy claim gone from all of them |
| 16 | Gate + run-and-observe | PASS except criterion 4 | see the row above |

### Harnesses written for this (kept, they are reusable)

- `observe.ps1` - drives Settings -> About -> Send through UI Automation (AutomationId, so language-independent).
- `observe-rtl.ps1` - same under Arabic, with the reports folder deliberately blocked; restores the language afterwards.
- `observe-recovery.ps1`, `observe-audio.ps1` - deterministic unstable-source runs (local HTTP harness, unresolvable host).
- `probe-language-list.ps1` - kept as the record of a dead end: the language picker's list is not reachable by `AutomationId` from a fresh UIA snapshot, which is why the RTL capture sets the language in the state file instead.

### Exit condition

The owner presses **Send logs to the author** once on a machine with a configured mail client and confirms the message carries `serzhyale@gmail.com`, the subject with the version, and the body naming the archive. Everything else is observed.
