# Contract-sync verification and catalog facets, 2026-10-22

This is the product's evidence record for SP-0239, the remainder of [SP-0232](SP-0232-contract-sync.md). It records
what each of the ten scoped items became after verification, what was implemented, and what is blocked on someone
else. It certifies no contract as a whole, authorizes no publication, and claims no run-and-observe result that
was not observed. Contract homes are resolved through [the agent-rules pointer](../../CLAUDE.md); no contract is
copied here. PLAN and temporary receipts are untracked, so everything a clone needs is recorded below.

## Disposition by item

| Item | Outcome | Evidence / owner |
| --- | --- | --- |
| 1. STREAM-BANK rule 14, item O (language and country closed sets) | **Blocked, not ported.** The catalog publishes no vocabulary table, no assigned-country list and no generated vectors for the two columns; the vocabulary exists only inside the producer's application code, which this product does not copy. No identifier is derived from OS locale tables. Topic half audited: the Windows consumer shows raw topic cells with case-insensitive lookup and never rewrites an unknown value to General; folding is a product decision, not a port. | Exit: the owner publishes the table and vectors (the 2026-10-20 proposal is unanswered). Stored selections, MANUAL and IMPORTED rows are untouched. |
| 2. INPUT-PARITY 0.4, VIEWER-KEYBOARD | **Implemented.** `PlayerKeys.Resolve` in Core decides; `PlayerWindow.Keyboard` performs `F`/`F11` full screen, `M` mute, `Ctrl+Up`/`Ctrl+Down` volume, `A` audio track, `R` record, `Esc` leaving full screen. Bare keys need no modifier and no auto-repeat; no key acts while a field, an open menu or an earlier handler owns the keyboard. File-only actions are not applicable to a live engine. `R` stays record where the contract says rotate: a recorded exception, not a rename. | 36 unit tests including negative cases. **Not observed:** keys with both engines, typing in the actions-menu text box, and whether the controls overlay window keeps keyboard focus after a click. Status `BlockNeedUserTest` for that clause. |
| 3. DEVICE-EXCHANGE DS-DU | **Verified by reading; DU gap deferred.** One own-account slot satisfies the outgoing-entry rule by construction. The server/state line, the no-entry note, retry after a refusal on a live session and the enrol-while-live announcement test all edit files SP-0237 still changes, and need owner wording (the vocabulary gate forbids "relay"; whether the line may show `host:port`). Nothing was forked. | Exit: SP-0237's audit phase is done, then the owner answers the questions in the plan. |
| 4. DOC-EXTERNAL-QUALITY 3, 8, 9 | **Partly implemented.** Portal control names are exact dictionary strings and a `terms` rule holds them; RU/UK portal mirrors and help translations carry a fingerprint of their English source and a `freshness` rule fails when the source moved; the help index must link every page. Portal scope, screenshots with markers and SEO applicability stay with the portal owner. | Local gates plus fixtures with mutation checks. Fingerprints were recorded as "in sync" from the current tree; no earlier fingerprint existed to prove it. |
| 5. CHECK-VERDICT legacy paths | **Partly implemented.** Gate scripts print the contract word `COULD NOT VERIFY` and exit `2` when a prerequisite throws; the nine fixture runners, `check.ps1`, `check-docs.ps1`, `check-help.ps1`, `build-help.ps1 -Check` and `build-site.ps1 -Check` follow it, `build-site` prints a verdict line, `make-demo -Check` reports `FAIL`/`1`, and a skipped optional check is counted on the verdict line. `Test-CheckVerdictScripts.ps1` (81 checks) pins this and fails against the pre-change scripts. | **Open owner decisions:** the first-failure stop at restore/build, the `Assert-*` throwers whose callers rely on a throw, `smoke-playback.ps1`'s verdict line, `Get-AuditCampaign.ps1` words, the legacy `PASS (N checks)` form. |
| 6. APP-ACTIVATION observation | **Blocked, procedure recorded.** Pipe protocol, names and bounds are covered by existing tests. Plain and targeted second start, a receiver process that is suspended, a UI thread that is suspended while the pool thread still acknowledges (judge against 7.C), a hung foreground window, another user in the same session, another session: all need an interactive desktop. Reading shows that another user in the same session gets the elevated-copy message, which names administrator rights misleadingly. No observation script was added because it could not be run or verified here. | `BlockNeedUserTest`. The frozen lock and pipe names are unchanged; an isolated profile changes them, so only the default profile proves the frozen identity. |
| 7. CAPTURE-OUTPUT rule 16 | **Measured in part.** A frame's file name and EXIF time come from one instant; a test with offsets +14:00, -12:00, +05:45, 0 and local finds them equal (6 tests). `scripts/Measure-CaptureTimes.ps1` (fixture: 21 checks) reads JPEG EXIF and MP4 `mvhd` back and prints `EQUAL`, `DIFF`, `NO-CARRIER`. Recording containers are written by the engine; `.ts` carries no creation time. | **Not measured:** fresh captures from both engines. One older recording on the owner's disk reads `DIFF` (container 261 s before the name); its origin is unknown, so it is a lead, not a finding. The contract is silent on UTC versus local for a container time: an owner question. |
| 8. WINDOWS-STORE | **Nothing submitted.** The evidence to keep at the next explicitly authorized submission is fixed: the draft export taken just before the import with SHA-256 and dialect, the builder run, the import report per language, a post-import re-export byte-compared with the uploaded file, the package read-back and the authorization reference. | Account-specific exports stay under `temp/`, never in tracked files. |
| 9. ICON-SET Unhide and More actions | **Unhide adopted.** The owner folded it into the vocabulary; the map now holds 64 meanings, no pending and nine private entries; the canonical glyph replaced the product's identical drawing. **More actions:** no owner response; the exception is wider than recorded (the product tooltip differs from the canonical name in ru, uk, fr, hi, bn and ar; the other seven match). No string was renamed. | `Sync-IconGlyphs.ps1 -Check` exit 0; iconography tests pass. |
| 10. LIVE-BROADCAST 1.3 item R, DEVICE-EXCHANGE 1.7 item DV | **Blocked by the ticket's own rule.** The producer writes `{schemaVersion, type, broadcast, maxListeners}` with no `auth`, `credentials` or `verifiers`, and has no password choice, so an empty array would invent consent. No password implementation was inferred. | Exit: the owner decides the generated/set/none choice, a joint plan with SP-0237 item 9, producer and relay negative and on-air tests, then the SP-0228 joint observation. |

## Owner questions and proposals to file

None of these was filed in the contract store from this repository. Each changes behaviour, a shared rule or a
translation, so it waits for the owner:

1. Publish the language vocabulary, the assigned-country list and generated vectors (item 1).
2. Is `R` as record acceptable against the contract's rotate, or should recording move to another key (item 2)?
3. Which UTC or local reading applies to a container creation time (item 7)?
4. Adopt the canonical More-actions names in six languages, or qualify them in a proposal (item 9).
5. DU wording: how to say "through the server" without the banned term, and whether the line shows `host:port` (item 3).
6. Should a topic fold exist on Windows, and may SEO clauses apply to offline help (items 1 and 4)?

## Verification

Environment: Windows x64, .NET SDK 10.0.402, Release configuration.

| Check | Expected | Actual |
| --- | --- | --- |
| `pwsh -NoProfile -File scripts/check.ps1` | All selected local gates pass | Exit 0, `check: PASS (20 checks, 1 skipped)`; `tests: PASS run=3606 skipped=0` (Core 3243, Exchange seams 346, Drive harness 17). The optional demo check is skipped by the existing empty-demo-folder rule and is now counted on the verdict line. |
| `Test-CheckVerdictScripts.ps1` | Changed gate paths hold | `run=81 skipped=0`; 37 checks fail against the pre-change scripts. |
| `Sync-IconGlyphs.ps1 -Check` against the catalog | Copies and derived files agree | Exit 0: 64 meanings, 0 pending, 9 private. |
| Key-map unit tests | Every row, every negative case | 36 passed. |
| Frame name/EXIF tests; `Test-MeasureCaptureTimes.ps1` | Equal across offsets; one case per verdict | 6 passed; `run=21 skipped=0`. |
| Docs and help gates | `terms`, `freshness` and index rules pass, and fail on a fixture | `docs-quality: PASS` with 47 control names and 2 mirrors; `help: PASS`; mutation of each rule makes the fixture fail. |

`PlayerWindow.xaml.cs` is still past the file budget (about 1500 lines) and is the standing exception; the key map
moved out of it. `tools/site/build-site.ps1` grew from 502 to 513 lines.

MANUAL / UNVERIFIED: every run-and-observe clause above, playback smoke (no media path, native dependency or
packaging payload changed, so it was not run), fresh live-bank acceptance, live Pages and Store import/re-export.
Whole-product conformance stays **Partial**. No commit, tag, push, package upload, local deployment or Pages
publication occurred, and nothing under the contract store was edited.
