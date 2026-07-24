# SP-0022: Audio sleep timer

**Status:** Verified

## Goal

Allow the listener to schedule automatic stopping of inline audio after a common duration or at a chosen local time.

## Why

A sleep timer is a small, understandable radio feature that adds value without expanding StreamsPlayer into recording, downloading, or media-library management.

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

## Implementation notes (SP-0022)

- `StreamsPlayer.Core/SleepTimerPlan.cs` (new) - the whole rule set as pure functions: preset
  deadlines, next-occurrence-within-24h clock resolution, clamped remaining time, idempotent expiry,
  `H:MM:SS`/`M:SS` formatting and 24-hour text parsing. 14 unit tests.
- `MainWindow.SleepTimer.cs` (new) - a clock button next to Stop in the now-playing bar; its menu
  offers 15/30/45/60 minutes, an inline `HH:mm` box, and Cancel while a timer runs. A one-second
  `DispatcherTimer` refreshes the countdown on the button and applies the deadline.
- Session rules: the control appears with the audio session and hides with it; the deadline survives
  a station switch (`StopAudioPlayback`), while a user Stop (`StopAudio`) and app exit drop it.
  Nothing is persisted, per the ticket''s non-goals.
- `Localization.en/ru/uk.xaml` - ten strings each; `App.xaml` - outline clock glyph.

Static checks: `dotnet build StreamsPlayer.sln -c Debug` -> expected 0 errors | actual 0 errors,
0 warnings. `dotnet test` -> expected green | actual 189/189.

## Verification - agent-driven UIA run (2026-07-24, Ukrainian UI)

- expected: the control appears only with inline audio | actual: hidden at idle, visible as
  `Таймер сну` once a station played, hidden again after the stream stopped.
- expected: presets and a clock entry | actual: menu showed `15/30/45/60 хвилин` plus the inline
  `Зупинити о (ГГ:ХХ)` box.
- expected: setting a timer shows the deadline and counts down | actual: status
  `Таймер сну встановлено на 13:40`, button showed `14:42` a few seconds later
  (`tmp/uia/shots/sp0022-countdown.png`).
- expected: cancelling clears it | actual: `Таймер сну скасовано.` and the button returned to its label.
- expected: a clock time stops playback once at the deadline | actual: timer set for 13:30, at 13:30:00
  the log recorded `SLEEP TIMER EXPIRED`, the status read `Таймер сну зупинив відтворення.` and the
  now-playing line returned to `Нічого не відтворюється`.
- expected: switching stations keeps the deadline | actual: a 30-minute timer read `29:47` after
  switching to another station (`tmp/uia/shots/sp0022-after-switch.png`).
- Not covered: resume-from-Windows-sleep is handled by the absolute-deadline design and its unit test
  (`Remaining`/`HasExpired` after a simulated sleep), not by a live suspend.
