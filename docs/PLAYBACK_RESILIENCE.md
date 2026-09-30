# Playback resilience: how StreamsPlayer fights a bad source

**Audience:** maintainers of this repository.
**Scope:** everything the product does to keep a picture on screen when the source, not the machine, is
the problem. It is a map of shipped behaviour with the constants and the file for each rule, so a
complaint about "freezing" can be routed to the layer that owns it.

For the outward-facing version of these findings - written for the FastMediaSorter (Android) developer
against the same stream bank - see [stream-playback-recommendations.md](stream-playback-recommendations.md).
That document argues *what* to do; this one records *what we did*.

---

## 0. The one thing to know first

**Most "freezes" on this stream bank are not one problem, and bigger buffers fix none of them.** Four
distinct failures were measured on real channels, and each has its own layer:

| Symptom | Real cause | Layer that owns it |
|---|---|---|
| Picture stops, audio stops, nothing arrives | source stopped sending | §3 freeze detection → §4 recovery |
| Picture stops but bytes keep arriving | decoder/clock fault | §2 open-time settings |
| Buffer empties every 10-60 s, forever | the chosen rendition is undeliverable | §5 quality ceiling |
| Nothing plays at all, ever | the channel or the network is down | §3a open budget → §4 recovery → verdict |
| Live drifts further behind after every stall, and never returns | nothing was holding the distance to the edge | §2 live-edge controller (optional engine only) |

Reconnecting to grow a buffer made things *worse* in every early measurement. The rule that follows from
that runs through the whole design: **decide from what was observed, not from what was declared.**

---

## 1. The measurement layer

Everything below is driven by six observations the player already makes. No layer adds a timer or a poll
of its own; each hangs off signals that were already there.

| Observation | Where it comes from |
|---|---|
| Buffering reached 100 % after playback was live → **stall**; buffering left → **resume** | `PlayerWindow.UpdateBuffering` |
| Displayed pictures and demuxed input bytes, monotonic totals | `IVideoBackend.ReadProgressCounters()` |
| Total bytes off the network, access layer and demuxer summed | `IVideoBackend.ReadReceivedBytes()` |
| Lost / corrupted picture counters | `IVideoBackend.ReadLossCounters()` |
| Media position, engine playing state | `IVideoBackend` |
| The engine's own error and end-of-stream events | `PlayerWindow` handlers → `PlaybackFailureSignal` |

Sampled on **one** two-second tick (`PlayerWindow.StatsSampleInterval`), which also writes the `STATS`
log line.

Two conventions make these rules testable and are worth preserving:

- **Time is a parameter, never ambient.** Every Core rule takes `now` from a monotonic stopwatch, so a
  wall-clock change can neither expire a threshold early nor hang it forever, and a test can drive ten
  minutes in ten lines. **Which** stopwatch is part of each rule's contract and is not interchangeable:
  rules that must span reconnects take `PlayerWindow.HealthNow` = `_sessionClock.Elapsed`, and the one
  per-leg rule (§3a's open budget) takes `_playbackClock`, which restarts on every open. Feeding the
  session clock to a per-leg rule would expire the second re-open the instant it began.
- **Null means "no evidence", never "fine".** A backend that reports no counters must not silently disarm
  a watchdog; it falls back to a weaker signal and says so.

Consequence: **every rule in this document lives in `StreamsPlayer.Core` and is unit-tested without a
window, a network, or a media engine.** The App only forwards observations and applies answers.

---

## 2. Open-time settings: avoiding the failure instead of recovering from it

`LibVlcVideoBackend` - instance options, fixed for every stream this backend ever plays:

```
--rtsp-tcp                 RTSP over TCP; UDP loss on public relays was unrecoverable
--clock-jitter=1000        tolerate broken PCR/PTS instead of stalling the clock on them
--avcodec-hw=none          software decode, always
--no-video-title-show --no-osd --no-snapshot-preview
```

Per-media options:

```
:network-caching=<ms> :live-caching=<ms>       15 000 live, 4 000 on a re-open
:rtsp-tcp                                       when the channel is RTSP
:adaptive-maxwidth=<w> :adaptive-maxheight=<h>  the SP-0071 ceiling (§5)
```

Three decisions here are load-bearing and have each already been re-litigated once:

- **`--avcodec-hw=none` is instance-wide, so it overrides `Play`'s per-stream `softwareDecode`
  argument.** Only `FlyleafVideoBackend` actually honours that argument today. Hardware decode caused GPU
  surface starvation, which was the original freeze. Do not "clean this up" without re-measuring.
- **`--clock-jitter=1000`, and `--no-ts-trust-pcr` was tried and reverted** - it removes the clock
  reference entirely and deadlocks the video output at 0 fps.
- **A smaller buffer on re-opens (4 s vs 15 s).** Flapping sources hit `EndReached` every ~20 s; refilling
  15 s of buffer on each one showed the spinner more than the picture.

### The live-edge distance controller - SP-0078, optional engine only

Every other rule in this document reacts to a failure. This one prevents a slow one: **nothing else keeps
the picture from drifting further behind the live edge after each stall.** The buffer refills, but the lag
accumulated while it was empty stays forever, and the only way back to the edge is a re-open - a black
screen paid once per stall for the life of the session.

`FlyleafVideoBackend` sets the engine's own latency controller once the open reports a live source:

```
Config.Player.MinLatency  6 s     return to normal speed here
Config.Player.MaxLatency  10 s    start speeding up here
Config.Demuxer.BufferDuration     raised by the engine to 20 s, overriding the 15 s / 4 s above
```

The corridor's three durations live in `Core/LiveEdgeCorridor.cs` and are unit-tested, because the engine
that consumes them is opt-in and rarely switched on: field evidence for this rule will be thin, so the
arithmetic is checked without it.

Four measured facts about the engine shape the whole design, all read out of FlyleafLib 3.10.4 on
2026-08-08 and none of them optional:

- **`Player.Speed` is not a usable seam.** Every accepted assignment sets `requiresBuffering`, so a rate
  loop of our own would stutter on each nudge. The engine's `MaxLatency` path is the only one that changes
  speed without re-buffering.
- **The correction floor is 1.1x and is not ours to lower.** The neighbouring Android product asks for
  1.02x; this engine cannot. The corridor's job is therefore to make the nudge *rare and short*, not
  small. Pitch is preserved - audio goes through FFmpeg `atempo`, which is live because
  `avfilter-11.dll` is already in `FFmpegComponents.RequiredLibraries`.
- **Peak correction is the buffer over the target**, so 20/10 = 2.0x at a full buffer. Past 4.0x the
  engine discards the queue instead of playing it out - a visible jump. `LiveEdgeCorridorTests` is what
  keeps a later edit to one of the three numbers from crossing that line silently.
- **`MaxLatency` raises the demuxer buffer to twice the target and sets `Decoder.LowDelay`.** This engine
  therefore holds 20 s of live buffer where the default engine holds 15 s, on live sources only. Clearing
  `MaxLatency` at the start of every `Play` restores both, so a non-live leg decodes exactly as before.

**Not on the default engine.** libVLC has no equivalent and cannot be given one; `--clock-jitter=1000`
(above) is a budget for the consequences of an uncontrolled offset, not a control over it. That asymmetry
is deliberate and is the cost of the feature: the same complaint may reproduce on one engine and not the
other.

---

## 3. Freeze detection - SP-0070

`src/StreamsPlayer.Core/PlaybackFreezeDetector.cs`. A stream can stop without the engine reporting
anything at all; nothing below §4 would ever fire.

**The rule is deliberately conservative: a freeze is _both_ signals at once** - nothing new reaches the
screen **and** nothing new arrives from the source, for `FreezeAfter` = **9 s**. Either alone is ordinary:
a rebuffering stream still receives bytes, a briefly-behind decoder still displays pictures.

Two fallbacks matter as much as the rule:

- Until displayed pictures have grown **at least once on this leg**, there is no picture signal to lose
  (audio-only, or the seconds before the first frame). Until then it judges by media time advancing
  `PositionProgressMilliseconds` = 500 ms.
- A backend reporting no counters falls back the same way. `null` is "this engine has no telemetry".

Input bytes are read from the **demuxer**, not the access layer: on HLS and DASH the segments are fetched
below the access layer, so the access-side count stays frozen for an entire healthy session.

Counters are differenced per media open and reset on each (`Reset()`), or the restart would read as either
a freeze or a burst of progress that never happened. A detected freeze reports **once**, so the next tick
does not fire a second recovery on the same event.

Second, cruder watchdog, still in `PlayerWindow`: buffering for **> 15 s** with media position advancing
< 500 ms → `WATCHDOG kind=stuck_buffer`. Different failure (a buffer that never fills, rather than a
picture that stopped), so it is a separate branch.

**Both branches are gated on the stream having been live at least once** (SP-0070 acceptance 4). That
gate is correct and stays, but it is also why §3a exists: for two years it left the entire pre-live
window watched by nothing at all.

---

## 3a. The open budget - SP-0096

`src/StreamsPlayer.Core/PlaybackOpenBudget.cs`. The exact inverse of §3 in scope: §3 watches a stream
that has played, this watches one that has not yet. Between them the session is covered with no overlap
and no gap - `ObserveOpenBudget` returns once `_reachedLive`, `WatchdogTimer_Tick` returns until it.

Why it was needed: measured on the owner's machine over 2026-09-06/08, **twelve sessions ended having
never played**, at 9.8 s, 16 s, 17 s, 24 s, 38 s and 65 s of black screen, every one of them carrying
`legs=1 | reconnects=0`. A source that raises neither an error nor an end-of-stream reached nothing in
this document, so the application did nothing and the user closed the window by hand.

Two branches, because "not answering" and "answering slowly" deserve different answers:

| Branch | Threshold | What happens |
|---|---|---|
| `DeadSource` - nothing has arrived at all | `DeadSourceAfter` = **8 s** | straight to the verdict, no re-open, no status probe |
| `Deadline` - answered, never reached the screen | `OpenDeadline` = **20 s** | `RecoveryTrigger.OpenTimeout`: one re-open, then the verdict |

The dead branch skips `RecoverAsync` entirely. Re-opening a host that has delivered zero bytes repeats
the silence, and `RecoverAsync` would first spend a `PlaybackStatusProbe` round trip on a host that has
just proved it answers nothing - adding seconds to the wait this rule exists to cut.

The input is `IVideoBackend.ReadReceivedBytes()`, and it is **both** byte counters summed - unlike §3,
which reads the demuxer's alone. The two swap roles at exactly the moment this rule cares about: while
an HLS stream is opening only the access-side count moves (the playlists come through it), and a logged
healthy open sat at `read_bytes=3046 | demux_bytes=0` for four seconds *including one tick after it went
live*. Once it plays, the access counter freezes instead. The budget only ever tests the total against
zero, never differences it, which is what makes summing them safe here and wrong anywhere else.

`null` counters (FlyleafLib, and the audio-only LibVLC engine on the radio path, which reads none) drop the dead branch and keep the
deadline. An engine that reports nothing must never be read as reporting zero.

Radio has the deadline only, as a `DispatcherTimer` in `MainWindow` (`AUDIO GIVEUP`), armed per leg in
`StartAudioPlayback` and disarmed in `StopAudioPlayback` - the one funnel every stop, switch, pause and
failure passes through. Before this a station whose URL never opened raised no `MediaOpened`, no
`MediaFailed` and no `MediaEnded`: the line read "Connecting.." indefinitely and `_audioWake` forbade the
machine to sleep for as long as it did.

`NotifyLive()` disarms the budget rather than the caller testing for liveness, so a stream that reached
the screen at 19.5 s cannot be taken off it by the observation at 20 s.

**The numbers are measured, not chosen.** Over 311 timed openings in the owner's two archives the median
first frame arrived at 2.4 s, p90 at 5.3 s, p99 at 11.2 s; only two channels exceeded 12 s (24 s and
31 s). Twenty clears p99 with room and costs exactly those two. Eight is a fourfold margin over what a
healthy open needs to move a byte counter at all. **Worst case for the user**: a source that answers and
never plays costs 20 s + 0.5 s + 20 s = 40.5 s before the verdict - against the 65 s measured on 2026-09-06
with no verdict at the end of it at all. A dead one costs 8 s.

---

## 4. Recovery - SP-0015, tightened by SP-0079

`src/StreamsPlayer.Core/LivePlaybackRecoveryPolicy.cs` decides; `PlayerWindow.RecoverAsync` executes.
`PlaybackRecoveryClassifier` turns an engine event into a `RecoveryTrigger` first. The budgets and the
backoff schedule come from the handoff brief this product was built from, not from an implementation
choice - `DEVELOPER_PROMPT.md` Part D (and Part F for backend adaptation), in the shared contract store
(see [contracts/STREAM-BANK.md](contracts/STREAM-BANK.md) for where that store is named). Change the table
below only after reading it.

| Trigger | Budget | Backoff before attempt _n_ |
|---|---|---|
| `BehindLiveWindow` | 3 | **_n_ x 0.5 s (0.5, 1, 1.5)** |
| `Transient` | **2** | **2ⁿ⁻¹ s (1, 2)** |
| `Stall` | 3 | **0.5 s** |
| `StreamEnded` | **2** | **0.5 s** |
| `OpenTimeout` | 1 | **0.5 s** |
| `HardFail` | - | no reconnect; straight to the verdict |

The table departs from the brief twice, and both departures are recorded only here.

**Budgets.** The two budgets in bold are **2 here and 4 in Part D**, on the owner's decision of
2026-08-08 (SP-0079). **Backoffs.** Every backoff is **half of Part D's** (transient 2 / 4 / 8 / 16 s,
behind-live 1 / 2 / 3 s, stall and end 1 s), on SP-0041 Decision 1: each leg already spends the engine's
own open timeout, so a longer pause bought little but more black screen, and the connectivity gate below
now settles the two cases a longer wait was hoping to outlast. SP-0041 itself said "three attempts at
1 / 2 / 4 s"; it predates SP-0079, so the later owner decision on the budget stands and only the halving
is applied. This paragraph is now the only record of
these divergences: it used to be annotated into a repo-local fork of the brief, which was deleted when the
brief's one home became the shared contract store. Part D binds nobody - it is the Android reference the
handoff was written from, not a rule of `STREAM-BANK` - so the departure is a product decision and not a
contract deviation. Four transient attempts on that backoff, on top of the engine's own ~26 s open
timeout per leg, is about two minutes of black screen before the user is offered Retry. SP-0072 put that
wait on screen where it can be read, which is what turned its length into the visible complaint. Nothing
is lost by stopping at two: the verdict dialog offers Retry, so a source that would have come back on
attempt three is one click away instead of ninety silent seconds away. `Stall` and `BehindLiveWindow`
keep their 3 - they re-open a stream that *was* playing, after a short pause, and cutting them
would break looping playlists that currently recover.

Budgets are **per trigger**, so a stream that stalls three times and then ends still has its
stream-ended budget. Sustained live restores the whole budget (`_recovery.NotifyLive()` on first live).
Exhausting a budget hands off to `PlaybackFailureDialog` - the terminal verdict, with Retry.

`OpenTimeout` is the newest and the smallest, and it is the only trigger the *player* raises rather than
the engine (SP-0096, §3a). One re-open, because it only ever fires after a full `OpenDeadline` has
already been spent staring at black, so every extra attempt costs another twenty seconds of exactly the
thing being fixed. Its backoff is a flat half second rather than the transient ladder, for the same
reason: the waiting has already happened.

### The connectivity gate - SP-0041

Before the ladder is spent on a **fresh open failure** - the one case where the status probe already
runs - `StreamReachabilityProbe` (App) establishes *what* is unreachable, and
`PlaybackReachabilityRules` (Core) says what follows. A stall, a stream end, a behind-live event, an
SP-0096 open verdict, or a status already in hand is never gated: that host was already answering.

| Verdict | How it is reached | Ladder | Hide/delete offer |
|---|---|---|---|
| `NotProbed` | not gated, or no probeable endpoint | runs | shown |
| `HostReachable` | TCP connect to the stream's own host:port succeeded | runs (then the status probe) | shown |
| `ChannelUnreachable` | that connect failed, and the catalog host answers | **skipped** - verdict now | shown |
| `NetworkUnreachable` | no interface up, the catalog host does not answer either, or a *local* host did not answer | **skipped** - verdict now | **withheld**, with a line saying the channel was never reached |

It connects rather than pings because CDN hosts drop ICMP while serving media, and the endpoint probed is
exactly the one the engine needs. Each connect is capped at 1.5 s, so the worst branch costs ~3 s against
the ladder it replaces. The only second host is the catalog host the app already downloads from - no new
outbound destination. A loopback, private-range, link-local, dotless or `.local`/`.lan` host is never
network-checked and never blamed (`StreamEndpointResolver.IsLocalHost`): a camera that is switched off is
not a broken channel. The verdict is logged as `PLAYBACK REACH` / `AUDIO REACH`.

The backoff is cancellable and the "Reconnecting" label stays visible through it: ordinary buffering and
reconnection must never look the same to the user.

Audio has its own parallel path in `MainWindow` (`AUDIO RECOVER`), same policy type.

---

## 5. Adaptive quality ceiling - SP-0071, SP-0076, SP-0077

The newest layer and the one with the most field evidence. `StreamQualityLadder`,
`AdaptiveQualityGovernor`, `QualityMemory`, `PlayingRendition` in Core; `StreamQualityLadderProbe`,
`PlayerWindow.Quality.cs` in App.

### The problem it solves

The reference channel offers three renditions and throttles per connection. Measured on one machine in one
hour, per 4 s of media:

| Rendition | Fetch time for 4 s of media | Verdict |
|---|---|---|
| 2096k / 1024x576 | 6.1 - 13.5 s | undeliverable |
| 796k / 640x360 | 2.0 - 5.0 s | borderline |
| 446k / 426x240 | 1.8 - 2.6 s | has margin |

The player sat on the top one. The buffer empties regardless of its size: a second of air is consumed per
second and delivered per two or three. **No buffer size fixes an under-delivered rendition** - only a
smaller rendition does.

### Why starvation, not a speed reading

Both cheaper measurements were tried against the evidence and rejected:

- **Delivered bytes vs the rung's declared `BANDWIDTH`** - `BANDWIDTH` is the *peak*, so a perfectly
  healthy rung reads 10-30 % under its own declaration. That is a false downgrade of a working stream.
- **An instantaneous rate sample** - this source bursts: `in_kbps` swung 0 → 2165 between two samples.

The buffer running dry *is* the measurement. It says delivery lost to real-time consumption over that
interval, whatever any number claims.

### The rule

- **Down:** two starvations inside `StarvationWindow` = **120 s** → step down exactly one rung. A
  starvation is a `PLAYBACK STALL` after live, or a caught freeze from §3. Two, not one, so a single
  hiccup never costs quality - which is also what keeps a healthy stream from ever reaching the rule.
- **A rung is what a cap can tell apart (SP-0130).** The engines cap by resolution - LibVLC by width and
  height, Flyleaf by height alone - and play the richest rendition that fits. The ladder therefore keeps a
  rendition only when no richer one fits under its height: same-resolution bitrates collapse to the
  richest, so no step re-opens onto the picture it left. Apple's bipbop sample: 24 variants, 6 rungs.
- **Clean means clean (SP-0130).** Any starvation restarts the interval a probe waits for, including one
  too isolated to step down.
- **Up is a trial, not a deduction.** On a lower rung the player is not saturating the link, so nothing it
  can measure proves the higher rung is deliverable now. The probe is a real switch.
- **A probe that never goes live is only a failed probe (SP-0130).** If the probed rendition errors, ends
  or hits the open budget before live, the player records the failure against that rung and re-opens
  the rung it left (`reason=probe_never_live_<trigger>`) - no open verdict, no recovery budget, no dialog.
- **The wait belongs to the rung, not to the player.** Base `FirstProbeAfter` = **5 min**, doubled once
  per recorded failure *of that rung*, capped at `MaximumProbeWait` = **1 h**. A probe is a success once
  it survives one full starvation window, and success forgives **only its own** rung.
- **A rung's record outlives the window.** `quality-memory.json`, keyed by normalized URL and by rung
  **bandwidth** (a source may re-encode), expires after **7 days**, capped at **200** sources.
  A record dated more than `FutureTolerance` = **5 min** ahead of the clock is expired too (SP-0130): it
  was written under a wrong clock and would otherwise never age.
- Every change is a re-open, because libvlc fixes adaptive options at media-open time. A downgrade
  triggered by a freeze rides the recovery re-open that was coming anyway and costs nothing extra.

### Opening at the remembered rung - SP-0076

The record used to seed only the probe waits, so every session still opened on the top rung and paid one
re-open - 3.0 to 18.5 s of black screen - to arrive where the previous session already knew it belonged.
The record therefore also carries **the ceiling that was in effect**, as a full rung, because the engines
take a ceiling as a *resolution* and the bandwidth key cannot be turned into one without a ladder.

- **Where the session opens** is what the last session settled on - `AdaptiveQualityGovernor.Ceiling`,
  which is null on the top rung, so an unrestricted session hands on no cap. SP-0130: written on every
  settled move - a plain step-down, a failed probe, a probe that survives its window - so a ceiling reached
  without any failed rung is remembered too; a probe still on trial is not.
- **Two lifetimes in one record.** Failure counts keep the 7 days; the ceiling is applied for
  `BlindCeilingRetention` = **24 h**. A count only changes how eagerly a rung is retried; a cap applied
  before a single observation decides what the user sees and he cannot know it happened. The day is also
  the backstop for "the source was fixed": full quality returns within a day even if no probe succeeds.
- **Nothing new is fetched before air.** The recall is a local file read started in the player window's
  constructor and awaited in `Loaded`, so it overlaps window layout. Measured: 0.9-1.5 ms between the
  recall line and the open, `ttff_ms` 343 (no record) vs 344 (record applied).
- **A miss cannot cost playback.** The cap is a guess about a ladder nobody has read yet, and libvlc's
  representation selector has nothing to fall back to when every rendition exceeds it. The first re-open
  of a session that has *never* reached live drops the remembered ceiling (`reason=miss`). Once the real
  ladder arrives, `UseLadder` enters at the highest rung inside the cap - the engine's own rule - and
  reports `entry=Missed` when none fits, which is today's behaviour plus a log line.
- **An older `quality-memory.json` still loads.** `Ceiling` is the last member and nullable, so a document
  from before this feature keeps its failure counts and simply caps nothing (`reason=no_ceiling`).

Deliberate limits, all logged rather than silent: HLS `.m3u8` only; every variant must declare both
`BANDWIDTH` and `RESOLUTION`; fewer than two rungs disables the rule; video only.

### Which rung is actually playing - SP-0077

Everything above is the *request*. The ceiling is a limit, not a choice: inside it the engine picks a
rendition itself and changes that pick mid-air, with no re-open and no notification. Until SP-0077 the
log held only the request, so "the rule applied" and "the rule applied and the picture came from the top
rung anyway" read identically, and a freeze could not be attributed to the rung it happened on.

`PlayingRenditionTracker` (Core) turns the engine's own reading into the few lines worth keeping;
`IVideoBackend.ReadRendition()` supplies it on the two-second tick that already carries four other
observations. Nothing is fetched, timed or polled, and nothing acts on the result - by decision, not by
omission: the signal is calibrated first, and wiring an uncalibrated observation into a rule that works
is how both get broken.

```
PLAYBACK QUALITY | action=rendition | cause=open|switch | from= | to= | ceiling= | within= | engine= | leg=
```

- `cause=switch` is a change the engine made inside one open; `cause=open` with a new `leg=` is a
  re-open. Those cost the user wildly different amounts and used to be indistinguishable.
- `within=` is `yes` / `no` / `no_ceiling` / **`unknown`** - four values rather than a flag, so an engine
  that reported nothing can never be read as an engine confirming the ceiling held.
- `to=unknown` once per leg is the engine saying nothing; `to=none | reason=not_video` once per session
  is a stream with no renditions at all.

**Reading it out of LibVLC is not what the API suggests, and the difference is measured.** Over 40 samples
of a healthy 1080p session, `MediaPlayer.VideoTrack` - documented as "current video track ID" - stayed at
`-1`, and `MediaPlayer.Size(0, ..)` kept returning `320x184`, the rendition the video output was first
built with, while the engine had long since climbed to 1920x1080 at eight times the data rate. It answers
`True` while doing so. What tracks reality is `Media.Tracks`, which is a **history** of the ES this media
has opened, with ids ascending as the demuxer opens them - so the highest-numbered video track is the one
on screen. FlyleafLib does expose `Player.Video.Width/Height`, undocumented in its package XML; FFmpeg
gives it each HLS rendition as a separate stream and it picks one at open, so there its answer is
expected to be constant for a leg.

That reading is also the first direct proof the ceiling is *obeyed* rather than merely passed: with a
remembered 848x480 cap the engine climbed and stopped exactly on it at 707-1084 kbps, where the same
source uncapped went to 1920x1080 at 7 163-11 155 kbps.

### What it measured, on the same channel and hour

| | before the rule | 60 s probe base | 5 min base, per-rung memory |
|---|---|---|---|
| session | 656 s | 656 s | 417 s |
| legs | - | 10 | 4 |
| stalls | - | 11 | 4 |
| black screen | - | 108.9 s (16.6 %) | 36.0 s (8.6 %) |

At the working rung the delivered rate sat at a steady 636-966 kbps at 24-28 fps for 290 s with no stall,
while the uncapped top rung swung 234-1131 kbps and then reported **0.0 kbps for sixteen consecutive
seconds**. That contrast is the whole feature.

Full evidence: `PLAN/SP-0071_adaptive_quality_ceiling/05_validation.md`.

---

## 6. What the user is told

- **The buffer stripe reports signal health by colour** (SP-0045, `SignalHealthMonitor`): green after
  `CleanInterval` = **60 s** undisturbed; any stall, caught freeze, recovery or failure disturbs it; loss
  counters growing by `LossThreshold` = **5** within one sample counts as trouble. The 60 s interval is an
  anti-flicker constraint, not a comfort setting: a stream dipping once a minute must read steadily
  yellow rather than strobe green between dips.
- **"Reconnecting" is shown only for an actual reconnect** - never for ordinary buffering, and never for
  a quality change, which is why `ApplyQualityDecision` deliberately does not go through `RecoverAsync`.
- **The terminal verdict** is `PlaybackFailureDialog`, with Retry and (only when the channel itself is at
  fault) an offer to hide or delete it.
- **A caption over the video whenever there is no picture** (SP-0072, `PlaybackInterruptionTracker`,
  `PlayerWindow.Notice.cs`). This is the answer to the field report "экран много раз моргает": a quality
  re-open used to be a silent black screen of 3-18 s, and the status line that could have explained it
  lives in a panel that hides itself after ten seconds of no mouse - so the viewer who was only watching
  never saw it. The caption sits in the same overlay layer as that panel but outside its hide timer, and
  names five states: connecting, signal lost, reconnecting with its attempt number, switching quality,
  and the terminal verdict. It appears only after `AppearDelay` = **1 s** of missing picture, which is
  the whole anti-flicker rule - a sub-second dip never puts anything on screen - and it clears the
  instant the picture returns, with no minimum visible time to hold it over live video. The delay runs
  from the start of the blackout, not from the last cause change, so a probe → attempt 1 → attempt 2
  recovery is one caption whose words change rather than three that flash.
- **The caption cannot reach a saved frame or a grid thumbnail**: both backends snapshot from the engine,
  never from the screen, so nothing in the WPF overlay is in the picture.

---

## 7. The log is the contract

`%LOCALAPPDATA%\StreamsPlayer\Current.log`, retired to `Session-<yyyyMMdd-HHmm>.log` on launch, last ten
kept. **A quality or freeze complaint is unreadable without it**, which is why each layer logs its
non-application as loudly as its application: "the rule did not apply" and "the rule never ran" must never
look the same in an archive.

| Event | Says |
|---|---|
| `PLAYBACK OPEN` | `reason=` (initial/quality/recover/retry), `cache_ms=`, `engine=`, `ceiling=` |
| `PLAYBACK LIVE` | `ttff_ms=` - the black-screen cost of that leg |
| `PLAYBACK SHOWN` | `frames=`, `at_ms=` - once per leg when the engine's displayed-picture counter first moves; LibVLC video writes LIVE only after this proof (SP-0133, SP-0163) |
| `AUDIO HEARD` / `AUDIO SILENT` | `played_buffers=`, `decoded_blocks=`, `lost_buffers=`, `after_live_ms=` - radio's output after `AUDIO LIVE`: a played buffer, or none within 20 s (SP-0133) |
| `PLAYBACK STALL` / `RESUME` | buffer emptied / refilled after live |
| `PLAYBACK WATCHDOG` | `kind=frozen` or `kind=stuck_buffer` |
| `PLAYBACK GIVEUP` | `rule=dead_source\|deadline`, `at_ms=`, `leg=`, `bytes=` - §3a decided this leg is not going to open |
| `AUDIO GIVEUP` | `rule=deadline`, `at_ms=` - the radio's half of §3a |
| `PLAYBACK RECOVER` | `trigger=`, `action=`, `attempt=`, budget, delay |
| `PLAYBACK QUALITY` | `action=recall\|ladder\|down\|up\|hold\|memory\|rendition`, `from=`, `to=`, `ceiling=`, `starvations=`, `memory=`, `within=`, `leg=` |
| `PLAYBACK CLOSE` / `SESSION` | `legs=`, `reconnects=`, `stalls=`, `outcome=` |
| `STATS` | every 2 s: `in_kbps`, `disp_fps`, `lost_pics`, `corrupted`, `discont` |
| `FLYLEAF LIVE EDGE` | `applied=yes` with the corridor, or `applied=no reason=not_live`; then `speed=`, `distance_ms=`, `cause=speed_changed\|heartbeat` |

**How to read a session in one pass:** sum `ttff_ms` over legs → total black screen; `legs` minus
`reconnects` → how many interruptions were quality changes rather than failures; `memory=` on the ladder
line → whether this session started knowing anything.

Since SP-0096, `outcome=never_live` without a `GIVEUP` line above it means one thing only: **the user
closed the window before the budget expired.** Every other way a session can fail to open now ends in a
verdict of ours, which is what makes that outcome readable instead of ambiguous.

Known noise: `direct3d11 | SetThumbNailClip failed: 0x800706f4`, about six lines per open. It is the video
output adjusting the taskbar preview clip. It is not a freeze and not a re-captured thumbnail - `THUMB
TAKEN` appears once per session.

---

## 8. Deliberately not done

| Not done | Why |
|---|---|
| A user-facing quality setting or per-channel quality preference | The user must not have to know what a rendition is. The failure *record* is not a preference. |
| Acting on the observed rendition - correcting a ceiling the engine did not honour | SP-0077 records `within=no` and stops there. No run has yet produced one, so any action would be built on a guess and would cost a 3-18 s re-open to apply it. Signal first, rule second. |
| Deriving the ladder from the engine's track list instead of the playlist | Measured and refuted: that list is a history of what has already played, not the declared set. A three-rung channel listed 426x240 and 1024x576 and never listed its middle rung; every track's bandwidth reads zero. |
| A seamless probe (fetching a segment of the higher rung and timing it) | Needs a second fetcher and steals bandwidth from the very link under test - it can cause the stall it is measuring. |
| A settle window after a leg starts | Looks obviously right, is wrong here: starvation is only reported after buffering reached 100 %, and the top rung emptied a *full* buffer 5-16 s after live. Any window long enough would have disabled the feature on the source that motivated it. |
| Bigger buffers | Measured, repeatedly, as no help. The failures are decode, clock and delivery-rate faults. |
| DASH ladders | Needs an MPD reader; a stream whose ladder cannot be read is left exactly as it is today, and says so (`reason=not_hls`). |
| Automatic background catalog downloads | Product rule, unrelated to playback but frequently proposed alongside it. |

---

## 9. File map

```
Core (platform-neutral, all unit-tested)
  PlaybackFreezeDetector.cs      §3  is this stream frozen        (after first live)
  PlaybackOpenBudget.cs          §3a is this stream ever going to open (before first live)
  LivePlaybackRecoveryPolicy.cs  §4  reconnect or give up, and after how long
  PlaybackRecoveryClassifier.cs  §4  engine event -> RecoveryTrigger
  PlaybackFailureSignal.cs           the input record
  SignalHealthMonitor.cs         §6  green / yellow / red
  PlaybackInterruption.cs        §6  what the caption says, and when it may appear
  StreamQualityLadder.cs         §5  HLS master playlist -> rungs
  AdaptiveQualityGovernor.cs     §5  which rung, and when to try higher
  QualityMemory.cs               §5  the record that outlives the window
  QualityMemoryStore.cs          §5  quality-memory.json
  PlayingRendition.cs            §5  which rendition is on screen, and when that changed
  LiveEdgeCorridor.cs            §2  how far behind live to sit, and what holding it costs

App (forwards observations, applies answers, owns all I/O)
  PlayerWindow.xaml.cs           the six observations, StartMedia, RecoverAsync
  PlayerWindow.Health.cs         §6  paints the stripe
  PlayerWindow.Notice.cs         §6  paints the caption over the video
  PlayerWindow.Quality.cs        §5  feeds the governor, logs it, re-opens
  StreamQualityLadderProbe.cs    §5  fetches the master playlist (5 s deadline)
  StreamReachabilityProbe.cs     §4  is it the channel or the network - the SP-0041 gate
  StreamTransmissionProbe.cs         what the engine received - ChannelInfoWindow only
  QualityMemoryFile.cs           §5  the one gate over the memory file
  LibVlcVideoBackend.cs          §2  engine options and the ceiling
  FlyleafVideoBackend.cs         §2  the opt-in engine, and the only home of the live-edge controller
```

## 10. Every number in one place

| Constant | Value | Where |
|---|---|---|
| Live buffer | 15 000 ms | `PlayerWindow.LiveCacheMilliseconds` |
| Re-open buffer | 4 000 ms | `PlayerWindow.ReconnectCacheMilliseconds` |
| Observation tick | 2 s | `PlayerWindow.StatsSampleInterval` |
| Clock jitter tolerance | 1000 ms | `LibVlcVideoBackend.ClockJitterMilliseconds` |
| Freeze threshold | 9 s | `PlaybackFreezeDetector.FreezeAfter` |
| Media-time progress | 500 ms | `PlaybackFreezeDetector.PositionProgressMilliseconds` |
| Stuck-buffer threshold | 15 s | `PlayerWindow.StatsTimer_Tick` |
| Dead-source threshold | 8 s | `PlaybackOpenBudget.DeadSourceAfter` (SP-0096) |
| Open deadline | 20 s | `PlaybackOpenBudget.OpenDeadline` (SP-0096; the radio timer's interval too) |
| Recovery budgets | 3 / **2** / 3 / **2** / 1 | `LivePlaybackRecoveryPolicy` (SP-0079; Part D says 3 / 4 / 3 / 4 and knows nothing of the fifth) |
| Caption appear delay | 1 s | `PlaybackInterruptionTracker.AppearDelay` |
| Health clean interval | 60 s | `SignalHealthMonitor.CleanInterval` |
| Health loss threshold | 5 per sample | `SignalHealthMonitor.LossThreshold` |
| Starvation window | 120 s | `AdaptiveQualityGovernor.StarvationWindow` |
| Starvations before step down | 2 | `AdaptiveQualityGovernor.StarvationsBeforeStepDown` |
| First probe after | 5 min | `AdaptiveQualityGovernor.FirstProbeAfter` |
| Maximum probe wait | 1 h | `AdaptiveQualityGovernor.MaximumProbeWait` |
| Quality memory retention | 7 days | `QualityMemory.Retention` |
| Quality memory cap | 200 sources | `QualityMemory.MaxSources` |
| Ladder fetch deadline | 5 s | `StreamQualityLadderProbe.Deadline` |
| Live-edge target / floor / buffer | 10 s / 6 s / 20 s | `LiveEdgeCorridor.Default` (Flyleaf only) |
| Live-edge correction floor / discard | 1.1x / 4.0x | `LiveEdgeCorridor.GentlestSpeed`, `.QueueFlushSpeed` (the engine's, not ours) |
| Live-edge log heartbeat | 60 s | `FlyleafVideoBackend.LiveEdgeHeartbeat` |

## 11. Tickets

| Ticket | Subject | Status as written in its header |
|---|---|---|
| `DONE/SP-0012` | buffered video backend for unreliable live streams | Verified |
| `DONE/SP-0015` | the bounded recovery ladder | Verified |
| `SP-0026` | selectable media backend | **Implemented** - awaiting the GUI run-and-observe |
| `SP-0041` | shorter recovery, connectivity-aware verdict | **Implemented** - the three verdicts not yet all seen on screen |
| `SP-0045` | the signal-health stripe | **BlockNeedUserTest** |
| `DONE/SP-0070` | silent freeze detection | Verified |
| `SP-0071` | adaptive quality ceiling | **Implemented** (not yet audited) |
| `SP-0072` | telling the user during an interruption | **Implemented** - two of five caption states not yet seen on screen |
| `DONE/SP-0076` | opening at the remembered rung | Verified |
| `DONE/SP-0077` | which rung is actually playing | Verified |
| `SP-0078` | holding the distance to the live edge | **Implemented** - the half-hour live run is not done |
| `SP-0079` | shorter reconnect budget | **Implemented** |
| `SP-0096` | a stream that never opens is given up on | **Implemented** - the run-and-observe phase is not done |

Three of those headers disagree with the folder they sit in. Status comes from the header, never from the
path - recorded here so the disagreement is visible rather than inherited.
