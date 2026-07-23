# SP-0020: Actionable failure dialog, hidden catalog channels, and copyable reports

**Status:** BlockNeedUserTest — all code implemented; Phases 1–4 done and Release `scripts/check.ps1` green (56/56 tests, 0 warnings). Exit condition: user runs the GUI checklist below (a user-owned instance PID 6492 was running, so a second instance was not launched — SP-0012 precedent). Plan: [SP-0020_hidden_channels_and_reports/INDEX.md](SP-0020_hidden_channels_and_reports/INDEX.md).

## Manual GUI checklist (run without another StreamsPlayer instance open)

1. Play a failing video/RTSP stream (e.g. Canal+ Foot) → dialog offers **Retry / Hide / Copy report / Keep**, not a bare OK.
2. On a **catalog** stream press **Hide** → it disappears; **Update catalog** and restart → still gone; the eye-off **Hidden** toolbar button appears.
3. Add a manual stream with a bad URL, play it, press **Delete** → confirm prompt → it is removed and does not return after **Update catalog**.
4. Fail an **audio** stream → same dialog; **Retry** re-attempts.
5. **Copy report** → paste elsewhere: contains version/UTC/title/kind/category and a redacted URL (no `user:pass@`, no `token=` value, no local path).
6. Open **Hidden** button → list shows hidden channels; **Unhide** one → it returns to the catalog with pin/order intact; **Close** changes nothing.
7. Switch EN⇄RU → dialog and Hidden window are localized.

Report `expected | actual` per item; set **Verified** only when all pass.

## Goal

Turn the dead-end playback-failure dialog into an actionable one: let users deal with a broken stream on the spot — retry it, make it go away, or keep it — and copy a concise diagnostic report for deliberate sharing. "Make it go away" is durable and origin-aware: catalog rows are hidden (a literal delete returns on the next refresh), user-owned rows are deleted.

## Why

A failed stream currently ends at an OK button, so the only outcome is dismissal, and the same broken catalog row keeps coming back on every refresh. Users need to clear clutter they did not create while keeping hand-made streams they intend to fix. Separately, the generic failure message gives maintainers too little evidence, so an explicit, bounded, copyable report is needed without automatic telemetry.

## Why hide, not delete, for catalog rows

Catalog refresh re-adds every catalog entry by URL, so deleting a broken catalog row is undone by the next refresh. Hiding — persisted by URL identity across refreshes — is the only durable way to make an unwanted catalog channel stay gone. Deletion is durable only for user-owned rows, which a refresh never re-adds.

## Non-goals

- Automatically send reports, logs, URLs, or analytics anywhere.
- Delete or mutate the published catalog or external bank.
- Run background channel monitoring, or probe/validate streams at import time — playability is only knowable from an actual play attempt.
- Remove or hide any stream silently; every removal is an explicit, confirmed user action.

## Constraints

- On a real playback failure the dialog replaces the dead-end OK with localized actions: **Retry** (re-attempt this stream), **Remove** (make it go away), and **Keep/Close**. This closes the documented Retry/Remove/Cancel failure-dialog contract.
- **Remove** is origin-aware, user-confirmed, never silent, and never triggered at import:
  - A `Catalog` row is **hidden**, persisted by normalized URL identity so an explicit catalog refresh does not bring it back.
  - A `Manual` or `Imported` (hand-made) row is **deleted** from local state; the merge never re-adds it.
- **Remove** is offered for every origin, but declining changes nothing, so a hand-made stream that is only temporarily down is never lost without consent. Delete and Hide are visibly distinguished so a user does not lose a stream they meant to fix.
- Hidden rows are excluded from ordinary views, search results, counts, preview capture, and playback navigation, but reachable in a dedicated manage-hidden view. Unhide restores the current catalog row without changing pin, order, collection, or play-mark semantics.
- A copied failure report contains app version, UTC time, channel title, catalog URL, media kind, and a stable error category; it excludes local paths, credentials, unrelated logs, and catalog contents. Copying is a visible user action and never transmits the report.
- `StreamsPlayer.Core` stays platform-neutral. Explicit-only catalog refresh and the `MANUAL`/`IMPORTED` merge protection remain unchanged; hide and delete operate on the local catalog state, not the external bank.

## Acceptance criteria

1. A real playback failure shows a localized actionable dialog (Retry / Remove / Keep) instead of a dead-end OK; Keep/close leaves state unchanged, and Retry re-attempts the same stream.
2. Remove on a `Catalog` row hides it: it leaves normal list/Grid views and stays gone after restart **and** after an explicit catalog refresh, without deleting the catalog row or mutating the external bank.
3. Remove on a `Manual`/`Imported` row deletes it from local state; it does not reappear after a refresh, and no user-owned row with a colliding URL is affected.
4. Users can view and unhide hidden rows; a channel absent from the latest catalog is cleaned up without an error, and unhide preserves pin/order/collection/play-mark.
5. From the same failure, the user can copy a localized, bounded report containing the defined diagnostic fields and no credentials or local paths; nothing is sent or saved remotely, and cancelling or ignoring the action changes nothing.
6. Merge, persistence, redaction, and removal tests pass, and localized retry / hide / delete / unhide / report flows pass run-and-observe checks.

## Risks

- Deleting a user-owned row is irreversible; the action must be explicit and clearly distinct from Hide so a hand-made stream meant to be fixed is not lost.
- URLs can contain credentials or private query values. Redaction takes precedence over diagnostic completeness, and hidden URL identities must remain compatible with the existing user-row-wins merge contract.
- Adding a Retry action must not duplicate or conflict with the auto-recovery budget in SP-0015; the dialog is the terminal hard-fail state, and Retry is a deliberate fresh attempt, not part of the automatic backoff.

## Open questions

- None blocking. Retry is included to fully close the documented failure-dialog contract; if it proves to overlap SP-0015's recovery, it can be reduced to Remove/Keep during tactical planning.

## Research

- See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md).
- Failure-dialog contract: `docs/specifications/streams.txt` (Retry / Remove / Cancel hard-fail dialog).
- Session decision trail: import-time filtering was rejected because playability is only knowable on a play attempt; broken catalog channels are cleared via user-confirmed hide (durable across refresh), user-owned rows via delete.
