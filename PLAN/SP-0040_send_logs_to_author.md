# SP-0040: Send diagnostic logs to the author

**Status:** BlockNeedUserTest - all nine phases implemented, the release-parity gate green (402 tests), and
fifteen of sixteen criteria observed. Exit: the owner presses **Send logs to the author** once on a machine
with a configured mail client and confirms the prepared message carries the address, the versioned subject
and the body naming the archive. The default handler here is a never-configured new Outlook, which opens its
account-setup screen instead of a compose window, so that one criterion cannot be observed on this machine.

## Goal

Give a user who hits a problem a one-click way, from the About tab of Settings, to
hand the author the diagnostic material needed to fix it: the application packs its
local log files plus a small environment summary into a single archive and opens the
user's default mail program with the author's address, a recognisable subject and a
prepared message body, so the user only has to attach the archive and press Send.

## Why

The product already writes a per-session diagnostic log (SP-0013), but nothing
connects it to the author. A report today is "it stopped playing" with no version,
no operating system, no media backend, and no log - so the first reply is always a
request for the file, and the file is usually gone by then (see Decision 2). The
information that makes a bug fixable exists on the user's disk and never travels.

The gap is also asymmetric in effort: locating `%LOCALAPPDATA%`, finding the right
file, zipping it and composing a mail is several minutes of instructions for a
non-technical user, and roughly one button for the application.

## Non-goals

- **No automatic, silent, or background sending.** Nothing leaves the device without
  the user pressing the button in that session, and no scheduled or crash-triggered
  variant is added.
- **No telemetry, no analytics, no upload endpoint.** The product does not gain a
  network path for diagnostics; delivery goes through the user's own mail program,
  under the user's own account, and the user sees the message before it is sent.
- **No in-app log viewer, no issue-tracker integration, no crash-reporter dialog.**
- **No user channel data in the archive.** The persisted catalog state, the user's
  `MANUAL` and `IMPORTED` entries, their URLs, pins, listening history and the
  cached preview images are out of scope for this transfer.
- **No new diagnostic subsystem.** Recording stays inside the existing app-side log
  facade and its established event vocabulary; this ticket completes the playback-quality
  trace (Decision 6) and changes retention (Decision 2), and does nothing else to
  diagnostics. No new severity levels, no structured log format, no log-level setting,
  no metrics store.
- **No mail credentials, no SMTP, no address book access.** The application never
  learns the user's address and never sends anything itself.
- **Do not weaken the explicit-refresh contract or the MANUAL/IMPORTED merge
  protection.** This feature touches neither.

## Decisions

1. **Delivery is a prepared mail plus the archive revealed in Explorer; attachment
   stays a user gesture.** A `mailto:` link cannot carry an attachment on Windows -
   the parameter is not part of the scheme and default mail clients ignore or reject
   it. Rather than reach for the legacy MAPI send API, which silently has no client
   on a webmail-only desktop and would add an interop surface to the app for a
   support feature, the flow does what always works: it writes the archive, opens the
   default mail program with recipient, subject and body pre-filled, and opens a file
   manager window with the archive already selected so attaching it is one drag or one
   copy-paste. The prepared body names the archive and its full path, so the mail is
   still useful if the user closes the file manager window.

2. **The previous session's log is retained, because that is the one worth sending.**
   Today each launch replaces `Current.log`. A user whose app misbehaved, closed it and
   then went looking for the send button has already destroyed the evidence - the exact
   scenario the feature exists for. On startup the existing log is therefore renamed to
   a previous-session log before the new one is created, and the archive carries both.
   This deliberately amends SP-0013's single-session rule; two sessions is the smallest
   retention that makes the feature work at all, and it keeps the "no multi-session
   history" spirit far closer than a rotating set would.

3. **The archive carries logs and an environment summary, nothing else.** The summary
   is a small plain-text file holding the application version, the Windows version and
   architecture, the selected interface language, the selected media backend and other
   diagnostically relevant settings, and catalog *counts* - never channel titles, URLs,
   or the state file itself. This is the information the author currently has to ask for
   in every exchange, and none of it identifies the user.

4. **The user is told what is being sent, before it is sent.** The About tab states in
   one line what the archive contains, and the flow leaves the composed mail and the
   archive open for inspection. Nothing in the flow is irreversible from the user's side.

5. **The feature degrades visibly, never silently.** If no log exists, if the archive
   cannot be written, or if no mail program is registered, the user gets a plain
   explanation and - where an archive was produced - its location, so the report can
   still be sent by hand. A support feature that fails quietly is worse than an absent one.

6. **The archive must let the author read how the player coped with a bad stream.** The
   owner's stated purpose for the archive is not only "it crashed" but "it stuttered":
   the log has to show, for each played channel, whether the player reached live, how
   often it stalled, what each recovery attempt decided, whether the attempt succeeded,
   and where the budget ran out. Most of that trace already exists for the video path;
   what is missing is closed here rather than left to a second ticket, because an archive
   that omits it does not serve the purpose the button exists for. Concretely: every
   playback session - audio as well as video - ends with one summary record stating
   whether it went live, how long it played, how many stalls and reconnects it took and
   how it ended; the alternate media backend, which today reports no buffering or error
   detail at all, gains the same stall and error records as the default one; and the
   environment summary states which backend produced the log, so the author knows what
   depth of detail to expect.

7. **The address and subject are product constants, declared once.** The author's contact
   address currently exists only in the website copy and the README, not in the
   application. It gains a single home in the app's product metadata alongside the
   existing author, source, website and privacy values, and the subject carries the
   product name and version so the mail is sortable on arrival.

## Constraints

- **Core stays platform-neutral.** Anything decided here that belongs in Core must be
  free of WPF, shell, file-manager and mail concerns; the archive assembly, the mail
  launch and the file-manager reveal are application-side.
- **No new logging surface.** The existing app-side log facade is the only diagnostic
  writer; this feature reads what it produced and must not introduce raw logging in
  Core or console output in the app.
- **The added quality records must not become noise or overhead.** They are session-scoped
  or event-driven, not per-frame or per-tick, so a long viewing session does not inflate
  the log the user has to mail, and no polling is added to the playback path.
- **Nothing in the quality trace may change playback behaviour.** Recording an outcome must
  not alter a recovery decision, a buffer target, or the user-visible labels.
- **Retention change must remain best-effort.** Renaming the previous log must never
  prevent the application from starting: a locked or undeletable file degrades to the
  current behaviour (previous log replaced) rather than to a failed launch.
- **The archive lives where the app may always write** - the same local application
  data area as the logs, or the user's temporary folder - never next to the installed
  executable, which is not assumed writable.
- **The archive must stay small enough to mail.** A session log is kilobytes today, but
  the flow must bound what it produces rather than assume that; oversized content is
  truncated or omitted, and the summary says so.
- **Repeated presses must not accumulate garbage.** Pressing the button twice must not
  leave a growing pile of archives in the user's local data.
- **Every user-visible string is localized in all thirteen shipped interface languages**,
  keeps its automation name in the active language, and survives the parity gate. The
  mail subject and body are user-visible text and are localized with the rest; the
  address is not.
- **Right-to-left layouts must absorb the new control** without clipping, and the About
  tab must stay readable at its current window size in the longest translation.
- **The About tab keeps its role.** It gains one action and one explanatory line, not a
  diagnostics panel; the settings window stays a UI coordinator.
- **The privacy statement must stop being false.** The published privacy text currently
  says local data stays on the device unless the user copies it; after this ticket that
  sentence has to describe this explicit, user-initiated exception - in every shipped
  site locale, generated from the single copy source, never hand-edited per page.

## Acceptance criteria

1. The About tab of Settings carries one clearly labelled action that sends logs to the
   author, with a tooltip and one line stating what the archive contains.
2. Pressing it produces a single archive containing the current-session log, the previous
   session's log when one exists, and the environment summary - and nothing else.
3. The environment summary carries the application version, the Windows version and
   architecture, the interface language, the media backend and other diagnostic settings,
   and catalog counts only. It contains no channel title, no URL, no user entry, and no
   copy of the persisted state.
4. The default mail program opens with the author's address as recipient, a subject naming
   the product and version, and a body that states the purpose and names the archive with
   its full path. Nothing is sent by the application itself.
5. A file manager window opens with the produced archive selected, so the user can attach
   it without navigating anywhere.
6. Nothing leaves the device without that button press: no automatic, scheduled, startup,
   or crash-triggered variant exists, and no network request is made by this feature.
7. After a restart, the archive still contains the log of the session that had the problem.
8. A launch whose previous log cannot be renamed still starts normally and still produces a
   working current-session log.
9. Two presses in a row leave at most one archive per attempt in a bounded location, with no
   unbounded growth across repeated use.
10. Missing logs, an unwritable archive location, and no registered mail program each produce
    a plain localized explanation, and the archive's location whenever one was produced.
11. Every new string exists in all thirteen interface languages, passes the localization
    parity gate, carries automation names in the active language, and does not clip or
    overlap in the mirrored layouts.
12. For an unstable stream, the archived log tells the whole story of a playback session
    without guesswork: the open, whether and when it reached live, each stall with its
    buffer level, each recovery decision with its trigger, attempt number, budget and
    delay, whether that attempt reached live again, the terminal failure when the budget
    is spent, and one closing summary per session naming the outcome, the watched
    duration, the stall count and the reconnect count. This holds for audio channels as
    well as video ones, and on both media backends.
13. The environment summary names the media backend in use, so the author can tell whether
    the log carries backend statistics or only session-level records.
14. The README and its Russian and Ukrainian mirrors describe the feature and what the
    archive contains.
15. The product site describes the feature in its usage section and corrects its local-data
    privacy statement, in every shipped site locale, produced from the single copy source.
16. Build and tests pass, and the flow is confirmed by run-and-observe evidence recorded as
    `expected: ... | actual: ...`: the archive is opened and its entries listed, the composed
    mail is seen with recipient, subject and body, the file manager window is seen with the
    archive selected, and a real unstable stream is played long enough for the archived log
    to show the stall-and-recovery trace of criterion 12.

## Risks

- **The user still has to attach the file.** Decision 1 trades a guaranteed-working path
  for one manual gesture. Some users will send an empty mail with no archive; the body
  naming the path is the mitigation, and the author can ask for the file by its stated
  name.
- **Default-mail behaviour varies.** Body length limits, plain-vs-HTML handling and even
  whether a client honours a subject differ between Outlook, Thunderbird, Windows Mail and
  browser-registered webmail handlers. The body must stay short and plain enough to survive
  the worst of them, and long text is the first thing to get truncated.
- **A user may have no mail program at all.** Criterion 10 covers it, but that user's report
  path is still manual.
- **Privacy perception.** A button that mails data to the author will read as telemetry to
  some users no matter how it behaves. The wording in the app, README and site has to be
  unambiguous that it is user-initiated, one-shot, and visible before sending.
- **The log content is the real privacy boundary.** The existing log deliberately retains
  full stream URLs for measurement, which means the archive can reveal which channels the
  user played. This ticket does not change what is logged, so what a user sends is exactly
  what the log already holds - but the disclosure text must not claim less than that, and
  narrowing the log's content is legitimate separate work.
- **Retention change touches startup.** Decision 2 adds a file operation to the launch path
  of every session, including sessions with no problem at all. It must not become a reason
  the app fails to start.
- **A stale archive misleads the author.** If a previous archive is reused or the reveal
  selects the wrong file, the author debugs the wrong session. Naming must make the session
  unambiguous.
- **Thirteen more strings.** As with every user-facing change after SP-0034, this costs
  thirteen translations plus the parity gate.
- **Audio quality detail is bounded by the platform.** The audio path uses a media element
  that exposes no buffer level and no position telemetry, so an audio session can report its
  open, its going-live, its failures, its reconnects and its outcome, but not "it stuttered
  for four seconds". Criterion 12 is satisfiable for audio only at that granularity, and the
  archive must not imply more.
- **The quality trace touches the recovery path.** Adding records inside stall and recovery
  handling is editing the most timing-sensitive, least unit-testable code in the product, on
  paths that already juggle a dispatcher, a cancellation token and a teardown flag. The
  constraint that behaviour must not change is the real risk here, not the records themselves.

## Open questions

None. Delivery mechanism (prepared `mailto:` plus revealing the archive in a file manager,
no MAPI interop), log retention (keep the previous session's log and archive both), and
archive contents (logs plus an environment summary, no persisted catalog state) were settled
with the owner on 2026-07-29. The archive's purpose was sharpened on 2026-07-30: the logs must
show how the player copes - or fails to cope - with a poor or unstable stream, which is now
Decision 6 and criteria 12-13.

## Implementation record - 2026-07-30

Nine phases, all `Implemented`; the per-phase `## Checks` blocks hold the evidence. Criterion verdicts are
tabulated in [09_validation.md](SP-0040_send_logs_to_author/09_validation.md); everything is observed except
criterion 4, which is this ticket's exit condition.

New in Core (platform-neutral, unit-tested): `DiagnosticLogFiles` (names + two-generation retention),
`DiagnosticEnvironmentSummary`, `DiagnosticArchiveBuilder`, `DiagnosticMailLink`. New in App:
`LogReportMailer` (shell only), `MainWindow.Diagnostics.cs` (the report action and the audio session
accounting), the About-tab control, and the session summaries on both playback paths.

Four things the plan did not anticipate:

- **Quitting the app while a station played recorded no audio session at all.** `MainWindow_Closed` does not
  pass through the stop funnel the plan relied on, so the one session a frustrated user would be reporting was
  the one with no summary. Found by observation, not by review, and fixed in phase 05.
- **The `mailto:` construction had to move to Core.** The prepared message cannot be observed on this machine,
  and escaping is exactly the part that fails silently - a raw `&` in a translated body truncates the mail with
  no error anywhere. It is now four unit tests instead of an unverifiable claim.
- **XAML collapses newlines in element content**, which would have flattened the mail body into one paragraph.
  The body carries literal `&#x0D;&#x0A;` entities in all thirteen dictionaries.
- **The mail subject keeps the Latin brand in Russian and Ukrainian**, against the glossary's rule for those two
  languages. The subject is the author's inbox filter; the in-app strings stay fully localized.

Deliberately not done: no Simple MAPI auto-attach (Decision 1), no redaction of the stream URLs the log already
records (spec Risks - narrowing the log's content is separate work), and no observation of the `FLYLEAF *`
records, which cannot run on this machine because the FFmpeg natives are absent and the factory falls back to
LibVLC.
