# StreamsPlayer Code Audit

The method for auditing code that did **not** change. `CODE_QUALITY.md` and `VALIDATION.md` judge a change and the
review skill reads a diff; neither says which unchanged file was read, how closely, or what was found in it. This
document does, for a pre-release campaign or any audit of existing code. It was written for SP-0139 and outlives it.

## Campaign shape

1. An **umbrella ticket** states the scope and the triage policy, and owns the campaign.
2. The **slicer** cuts the audited files into slices and writes a manifest: each slice's files, lines, read-for-
   context files, sibling slices, risk signals and risk score. It is deterministic; re-running it on the same tree
   writes the same bytes.
3. The **fan-out** creates one self-contained ticket per slice, in risk order, idempotent by slice name.
4. Each slice ticket runs the **slice procedure** below and closes with a `## Last Audit` block in a fixed shape.
5. The **summary** reads the manifest and the slice tickets and prints coverage, slices by status, findings by
   severity and the tickets the campaign produced. Its exit code says whether the campaign is closed.

```powershell
pwsh -NoProfile -File tools/audit/New-AuditSlices.ps1 -Parent SP-NNNN            # manifest (exit 0 / 2)
pwsh -NoProfile -File tools/audit/New-AuditSliceTickets.ps1 -Parent SP-NNNN      # tickets (idempotent)
pwsh -NoProfile -File tools/audit/Get-AuditCampaign.ps1 -Parent SP-NNNN          # state (exit 0 closed / 1 open / 2)
pwsh -NoProfile -File tools/audit/Test-AuditTools.ps1                            # the tools' own fixture tests
```

Files added after slicing show up in the summary as uncovered; `New-AuditSlices.ps1 -FileList` slices them into
a tail manifest that the fan-out and the summary accept like the first.

## What is audited

- **Class A** (always): every `.cs` and `.xaml` of the library and the application except the localization
  dictionaries; the console harness; the build, run, check, release and smoke scripts; the CI and release
  workflows; the MSBuild files, installer script and package manifests that shape what ships.
- **Class B** (behind `-IncludeClassB`): tests, localization dictionaries, site and Store tooling, these tools.
  Tests and dictionaries already have their own gates; the switch changes membership, never the slicing.
- The authoritative lists are the rule tables at the top of `tools/audit/AuditCampaign.psm1`.

## Slicing rules

- A markup file and its code-behind are never separated. A partial-class family stays in one slice while it fits
  the limits; otherwise it is split by file, and every part that does not audit the family root lists the root as
  read-for-context. Parts of one family name each other as siblings, so a reader can look sideways without
  auditing.
- The library never shares a slice with the application, so the library's boundary rule is judged with the
  library as a whole.
- Limits are parameters (defaults 25 files, 4000 lines). A file over the ~500-line budget is a risk signal, not a
  finding; a file with more than one reason to change is.
- Risk is every counted signal per thousand lines, the divisor floored at a quarter of the line limit. The signals
  are `async void`, event and timer subscriptions, native-engine and `IDisposable` ownership, dispatcher
  marshalling, locks and static mutable state, network reads, file writes, process launches, untyped `catch`,
  the null-forgiving operator and oversize files; tooling counts destructive steps, publishing, network,
  process launches, swallowed failures and forcing flags.

<!-- slice-procedure:begin -->
### Slice procedure

1. **Pre-scan.** The build and test baseline is green (`pwsh -NoProfile -File ./build.ps1 -Test -Deploy:$false`,
   exit 0); the manifest's signal counts for the slice's files are the reading list's first hints; the earlier
   audit dossiers' findings for the same files are known and not rediscovered. A green baseline is a starting
   point, never a verdict.
2. **Layered read.** Every audited file is read line by line, aimed at what no gate sees:
   - *Architecture and cohesion* - the library stays platform-neutral; dependency direction App -> Core,
     Harness -> Core, Tests -> Core; one reason to change per type; logging only through `CurrentLog` in the
     application, none in the library, console output only in the harness.
   - *Lifetime and concurrency* - who owns each subscription, timer, native handle and cancellation source, and
     whether its release is symmetric with its acquisition on every exit path, including exceptions and window
     close; every `async void` guarded; no UI-thread access from a worker; no teardown into a disposed engine.
   - *Failure handling* - the narrowest catch, then a documented safe default or a rethrow; nothing swallowed;
     a failed load is never followed by a save over it.
   - *Resource bounds* - every network read has a deadline and a size ceiling; every cache and log has a bound;
     no work on the UI thread that grows with the catalog.
   - *User data and contracts* - user-authored values survive every merge, import and refresh; each shared-
     contract rule is implemented where the contract's pointer in `docs/contracts/` says it is, judged against
     the contract, not the code's own comments. A deviation is a finding whose fix is "comply or amend"; an
     amendment is never made inside a slice.
   - *Outward-facing safety* - nothing personal or secret written to a log, a launched process or an archive;
     only a launchable address is launched.
   - *Tooling slices only* - a release publishes exactly what was built from the tag; a gate fails when what it
     guards is missing; no destructive step behind a skipped check; frozen anchors never change.
3. **Record.** Each finding gets one row in the slice's `research/findings.md`: file and line, layer, severity
   (High / Medium / Low), confidence (`confirmed` - traced end to end; `plausible` - the path is traced, the
   trigger needs timing, data or a run), what is wrong and what it costs the user, and the specification that
   owns it. The coverage table there has one row per audited file - read in full, and how many findings - so
   the slice can say "this file was audited".
4. **Triage.** Before a finding gets a new ticket, search `PLAN/`, `PLAN/DONE/` and the audit dossiers under
   `temp/` for the symptom. A ticket that already owns it gets a link and, where the slice found more, one added
   line of evidence or one added requirement. Otherwise the finding gets a specification of its own - several
   findings with one symptom share one ticket - written as `Draft` with Goal, Why, Requirements, Acceptance
   criteria and Source, and placed in the release queue by what the defect costs the user. The umbrella
   ticket's triage policy decides whether a finding may instead be fixed inside the slice; where it may, only
   under the inline-fix rule: the change is in the library with a passing test for the affected type, or it
   preserves behaviour by construction. Never inside a slice: a change to persisted state's shape, a new
   interface string, a change to a window, a media backend, a release script or a workflow.
5. **Close.** The slice ticket ends with a `## Last Audit` block in exactly this shape, which the summary parses:

   ```text
   ## Last Audit
   **Date:** YYYY-MM-DD
   **Coverage:** <read> of <audited> audited files read in full
   **Findings:** High <n> | Medium <n> | Low <n>
   **Tickets:** SP-NNNN, SP-NNNN   (every ticket created or extended; `none` when there are no findings)
   **Inline fixes:** none   (or each fix with its `expected: X | actual: Y` evidence)
   ```

   The slice is `Verified` when every audited file is read, every finding has its specification, and any inline
   fix has its evidence. A slice never runs a release, a deploy or anything that publishes.
<!-- slice-procedure:end -->

## After the campaign

The summary's output becomes the umbrella ticket's own `## Last Audit`. The umbrella closes when the summary
exits 0: every slice closed, no uncovered file, and every High and Medium finding owned by a ticket. The next
campaign is a new umbrella ticket with a new manifest; a closed one is not reopened.
