# PHASE-3 — Audio recovery wiring (MainWindow MediaElement path)

**Produces:** bounded audio reconnect-with-backoff, Reconnecting now-playing label, and session
cancellation in `src/StreamsPlayer.App/MainWindow.xaml.cs` (+ `MainWindow.NowPlaying.cs` if cleaner).
**Consumes:** Phase 1 (`LivePlaybackRecoveryPolicy`, `PlaybackFailureSignal`).

Backend stays WPF `MediaElement` (SP-0026 non-goal). No position stall watchdog for audio —
`MediaElement` exposes no reliable live position/byte telemetry; scope is transient-failure reconnect.

## Steps

1. **Per-audio-session policy + cancellation.** Add `private LivePlaybackRecoveryPolicy? _audioRecovery;`
   and `private CancellationTokenSource? _audioRecoveryCts;`. Create both in `PlayChannelAsync` when an
   audio play starts (fresh policy per new channel). Static check: build.

2. **Cancel on stop/switch/close.** In `StopAudioPlayback` cancel + dispose `_audioRecoveryCts`, null both
   fields (AC 4 — stop and channel-switch both funnel through `StopAudioPlayback`). Confirm
   `MainWindow_Closed` stops audio. Static check: build; grep that every audio-stop path clears the CTS.

3. **Recover on `AudioPlayer_MediaFailed`.** Replace the immediate-dialog body with:
   - Build `new PlaybackFailureSignal(reason, HttpStatusCode: await ProbeStatusAsync(channel.Url))`
     (reuse a shared status-probe helper; http/https only, short timeout, linked to `_audioRecoveryCts`).
   - `var decision = _audioRecovery.Decide(signal);`
   - `Reconnect`: `SetNowPlaying("ReconnectingAudioAttempt", title, attempt, budget)`;
     `await Task.Delay(decision.Delay, token)` (cancelled -> return, stay stopped);
     if still the current channel and not cancelled, re-invoke the audio start (re-`Play` the same channel
     without toggling stop). Guard against the re-entrant "re-tap stops" branch.
   - `HardFail`/exhausted: existing terminal `PlaybackFailureDialog` flow (Retry / Copy / Hide|Delete / Keep).
   - Static check: build.

4. **Reset budget on live.** In `AudioPlayer_MediaOpened` call `_audioRecovery?.NotifyLive()` so a station
   that reconnects successfully restores its full budget. Static check: build.

5. **Reconnecting label.** New now-playing strings `ReconnectingAudioAttempt` (Phase 4). Ensure the ICY
   now-playing reader (`_nowPlayingGeneration`) does not overwrite the Reconnecting label during a
   backoff wait (generation already guards superseded readers; verify a reconnect starts a new generation).
   Static check: build.

## Phase static check

`dotnet build StreamsPlayer.sln -c Release` — expected: succeeds. (Run-and-observe is Phase 5.)
