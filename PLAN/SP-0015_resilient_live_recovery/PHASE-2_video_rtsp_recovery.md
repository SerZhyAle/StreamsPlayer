# PHASE-2 — Video/RTSP recovery wiring (PlayerWindow)

**Produces:** recovery coordinator, stall watchdog, failure-path HTTP status probe, session
cancellation, and the Buffering-vs-Reconnecting label in `src/StreamsPlayer.App/PlayerWindow.xaml.cs`.
**Consumes:** Phase 1 (`LivePlaybackRecoveryPolicy`, `PlaybackFailureSignal`).

Replaces the ad-hoc `_endReconnects` loop and the immediate-fail-on-error path with the Part D policy.
Preserves the existing `_mediaGate`/`_closing` teardown discipline and off-UI-thread Stop/Dispose.

## Steps

1. **Session cancellation.** Add `private CancellationTokenSource _sessionCts = new();` Cancel + dispose
   it in `PlayerWindow_Closed` (before the off-thread teardown) so any pending backoff `Task.Delay`
   aborts and no superseded reconnect runs. Every recovery wait observes `_sessionCts.Token`.
   Static check: build.

2. **Policy instance.** Add `private readonly LivePlaybackRecoveryPolicy _recovery = new();` Call
   `_recovery.NotifyLive()` in `UpdateBuffering` on the sustained-live branch (where `_endReconnects`
   is currently reset). Remove the `_endReconnects` field and its `Interlocked` uses. Static check: build.

3. **Recovery coordinator.** Add `private async Task RecoverAsync(PlaybackFailureSignal signal)`:
   - Marshal to UI thread if needed (mirror `ShowPlaybackFailure`).
   - `var decision = _recovery.Decide(signal);`
   - `HardFail` -> call the existing terminal path `ShowPlaybackFailure(signal.Reason ?? "recover_exhausted")`
     (shows `PlaybackFailureDialog`: Retry / Copy / Hide|Delete / Keep — AC 5).
   - `Reconnect` -> set the Reconnecting label (step 6) with `decision.Attempt`/`decision.Budget`;
     `await Task.Delay(decision.Delay, _sessionCts.Token)` (catch `OperationCanceledException` -> return);
     if `!_closing` `await Task.Run(() => { if (!_closing) StartMedia("recover"); })`.
   - Static check: build.

4. **Route backend signals through the coordinator.**
   - `MediaPlayer_EncounteredError`: replace `ShowPlaybackFailure("encountered_error")` with
     `_ = RecoverAsync(await BuildSignalAsync("encountered_error"))` (probe from step 5).
   - `MediaPlayer_EndReached`: replace `ReconnectAfterEndAsync` body with
     `_ = RecoverAsync(new PlaybackFailureSignal("end_reached", EndReached: true))`. Delete
     `ReconnectAfterEndAsync`.
   - Behind-live-window: if a VLC log line (`LibVlc_Log`) matches a live-window token, set a flag so the
     next signal carries `BehindLiveWindow: true`. (Best-effort; default path still recovers as Transient.)
   - Static check: build.

5. **Failure-path status probe.** Add `private async Task<PlaybackFailureSignal> BuildSignalAsync(string reason)`:
   - For `http`/`https` `_channel.Url`: one `HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, url),
     ResponseHeadersRead, cts)` with a ~5 s linked-to-`_sessionCts` deadline; capture `(int)StatusCode`.
     On probe exception -> null status (stays Transient). Use a single static `HttpClient` reused across
     the window (own the instance; do not touch MainWindow's). Never probe RTSP.
   - Returns `new PlaybackFailureSignal(reason, HttpStatusCode: status)`.
   - Static check: build. Rationale: only way to satisfy AC 1/AC 2 for VLC-hidden HTTP status.

6. **Stall watchdog.** Replace the buffering-event-only `_isStalled`/`_stallCount` recovery gap with a
   3 s `DispatcherTimer _watchdogTimer`:
   - Track last `_mediaPlayer.Time` and last input bytes (`_mediaPlayer.Media?.Statistics`).
   - Freeze A: state == Playing and `Time` advanced < 500 ms for 3 consecutive ticks (~9 s).
   - Freeze B: continuously buffering > 15 s with no byte-count progress.
   - On A or B while not already recovering: `_ = RecoverAsync(new PlaybackFailureSignal("stall", Stall: true))`
     and reset the watchdog counters. Genuine rebuffering (bytes moving, < 15 s) does nothing (tuning §4).
   - Start in `PlayerWindow_Loaded`, stop/unsubscribe in `PlayerWindow_Closed`.
   - Static check: build.

7. **Buffering-vs-Reconnecting label.** `UpdateBuffering` keeps `BufferingProgress`/`PlayingLive`. Recovery
   sets `WaitText` to the new `ReconnectingAttempt` string (Phase 4) formatted with attempt/budget. A
   guard flag (`_recovering`) prevents a stray Buffering event from overwriting the Reconnecting label
   mid-backoff; cleared when live is reached or the window recovers. Static check: build.

## Phase static check

`dotnet build StreamsPlayer.sln -c Release` — expected: succeeds, no warnings introduced by these files.
(Run-and-observe of actual recovery is Phase 5.)
