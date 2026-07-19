# SP-0022: Audio sleep timer

**Status:** Approved

## Goal

Allow the listener to schedule automatic stopping of inline audio after a common duration or at a chosen local time.

## Why

A sleep timer is a small, understandable radio feature that adds value without expanding StreamPlayer into recording, downloading, or media-library management.

## Non-goals

- Control video or RTSP windows.
- Wake or launch the application, schedule recording, or resume playback automatically.
- Persist a timer across application restart.
- Add a general task scheduler.

## Constraints

- Available choices are 15, 30, 45, and 60 minutes plus one user-selected local clock time within the next 24 hours.
- One timer exists for the active inline audio session. Switching audio stations keeps the deadline; manual Stop or explicit Cancel timer clears it.
- Expiry stops audio through the normal stop path and explains that the sleep timer ended playback.
- Closing or restarting the app discards the timer; system sleep does not extend its absolute deadline, and an expired deadline is applied when the app resumes.
- Remaining time and cancellation are accessible without opening a new full-size screen.

## Acceptance criteria

1. The user can start each preset or a valid local-time timer while inline audio is active and can see the remaining time/deadline.
2. At expiry, the active audio stream stops once and the UI returns to the normal stopped state with a localized explanation.
3. Switching stations preserves the deadline; manual stop, cancel, or app exit removes the timer and prevents a later action.
4. Invalid or already-passed local times are rejected or resolved to the next occurrence within 24 hours without ambiguity.
5. Resume after Windows sleep applies an elapsed timer promptly without starting playback or affecting video windows.
6. Timer/state tests and an accelerated run-and-observe expiry/cancellation check pass.

## Risks

Wall-clock changes and Windows sleep can shift local-time calculations. The user-visible deadline must remain predictable while expiry stays idempotent.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md).
