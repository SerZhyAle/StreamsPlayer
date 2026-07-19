# SP-0015: Resilient live-stream recovery

**Status:** Approved

## Goal

Recover audio, video, and RTSP playback from temporary network failures and silent stalls while clearly distinguishing ordinary buffering from reconnection.

## Why

Public live streams are routinely interrupted by relay, network, and live-window failures. A bounded recovery policy is more useful than immediately failing or spinning indefinitely without explaining the state.

## Non-goals

- Guarantee availability of a third-party stream.
- Refresh or replace catalog data automatically.
- Retry permanent client errors, malformed media, or unsupported formats indefinitely.
- Add background health monitoring for channels that the user is not playing.

## Constraints

- Recovery follows the retry budgets, backoff, live-edge, buffering, and stall-watchdog rules already fixed in `streams.txt`, Part D.
- The UI distinguishes localized Buffering and Reconnecting states and remains cancellable.
- A new user playback choice cancels recovery of the previous stream.
- Exhausted or non-retryable failures produce one terminal outcome and a clear retry action; removal is available only when the channel's provenance permits it.
- Grid-preview failures never trigger foreground recovery or write a failed-play mark.

## Acceptance criteria

1. Retryable connection, timeout, HTTP 429/5xx, behind-live-window, and silent-stall scenarios use the specified bounded recovery policy.
2. Explicit non-429 4xx responses, malformed manifests, and unsupported containers fail without consuming the transient retry budget.
3. Buffering and active reconnection are visibly distinct in English and Russian, including the current attempt outcome.
4. User stop, window close, or selection of another stream cancels outstanding waits and prevents the old stream from restarting.
5. Exhausted recovery exposes Retry and Close; Remove is offered only for removable user-owned rows.
6. Automated policy/state tests pass, and run-and-observe checks cover successful recovery, cancellation, and terminal failure for representative live media.

## Risks

Media backends report failures and progress differently across HTTP audio, HLS/DASH video, and RTSP. Recovery must avoid duplicate playback sessions and must not confuse a slow initial buffer with a frozen stream.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md) and the recovery contract in [streams specification](../docs/specifications/streams.txt).
