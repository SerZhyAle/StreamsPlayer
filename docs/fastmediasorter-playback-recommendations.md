# What StreamsPlayer could take from FastMediaSorter (Android/Media3)

**Audience:** maintainers of StreamsPlayer (Windows, .NET/WPF, libVLC).
**Source:** FastMediaSorter, the Android app that publishes the same stream bank, running AndroidX Media3
(ExoPlayer) 1.2.1.
**Written in reply to** `docs/PLAYBACK_RESILIENCE.md` (2026-08-08 revision). That document is the reason
this one exists: it is specific enough to compare against, including its negative results, which is rare
and worth saying out loud.

Same convention as yours: this is what we shipped and measured, not what sounds right. Where a claim is a
pointer rather than a verified fact, it says so.

---

## 0. The short version

Four things below. Ordered by what they would buy you, not by how easy they are:

0. **Neither of us knows which rung is actually playing** - only which rung we capped at. That gap makes
   your §5 probe-success verdict recordable when the engine never climbed, and it is closed by events
   both engines already emit. New since the first revision of this document; see §2a.
1. **Your quality ladder can probably come from the demux track list instead of a second fetch of the
   master playlist** - this would remove three of the four "deliberate limits" in your §5 at once,
   including the DASH one.
2. **A quality change that costs no re-open changes the arithmetic of your §5.** Ours costs nothing, and
   that is why our probe schedule will not look like your 5 min -> 1 h ladder. If you cannot make the
   change free, your §8 "apply the remembered ceiling at open time" stops being a candidate and becomes
   the only cheap option you have.
3. **A per-channel outcome that has a third state.** Not OK/FAIL - OK/FAIL/UNKNOWN, where UNKNOWN means
   "the probe was inconclusive". It is what stops a reachability sweep marking a channel dead because of
   the user's own network. It is the persistent counterpart of your SP-0041.
4. **The §11 problem - three ticket headers disagreeing with their folder - is structural, and it has a
   structural fix.** Status must not be inferrable from a path, because then a path can contradict it.

And one layer you cannot copy but should know exists, because it explains why our §3-equivalent is much
smaller than yours: Media3 gives us direct control of the distance to the live edge.

---

## 1. The ladder probably does not need a second fetch

Your §5 lists four deliberate limits: HLS `.m3u8` only, every variant must declare both `BANDWIDTH` and
`RESOLUTION`, fewer than two rungs disables the rule, DASH needs an MPD reader you have not written. All
four follow from one decision - that `StreamQualityLadderProbe` fetches and parses the master playlist
itself, with a 5 s deadline.

We do not parse anything. The ladder is read off the engine's own track list once the manifest is already
loaded, in the callback that fires when the track set changes. Consequence: the same code path covers HLS
and DASH with no format knowledge, there is no second network fetch, no deadline to tune, and no
dependency on what the manifest chose to declare - a rendition with no advertised bitrate still has a
resolution, and we cap on both, treating the missing bitrate as "no bitrate cap" rather than dropping the
rung. Your reference channel's log in §5 shows exactly the case that breaks a parser and not a track list.

**Pointer, not a verified claim:** libVLC's adaptive demux necessarily knows the representation set - it
is choosing among them. Whether that set is reachable from the outside is the question worth an hour:
start with the track/ES description API and with what the adaptive module exposes per-elementary-stream.
We have not verified this on libVLC and cannot; if the answer is no, the rest of your §5 stands as
written and this section is void.

If the answer is yes, it deletes the 5 s deadline, the `not_hls` branch, the both-fields-required rule,
and the DASH gap - four entries in your own "deliberate limits" and "deliberately not done" lists.

## 2. Your probe schedule is a cost of the re-open, not a law

The most expensive thing in your document is not a threshold. It is this line in §5: *"Every change is a
re-open, because libvlc fixes adaptive options at media-open time."* Everything downstream is paying for
it - the 3-18 s black screen, the 5 min probe base, the doubling, the 1 h cap, and SP-0072 still sitting
in Draft because there is nothing to tell the user during a silence you cannot avoid.

On our side the ceiling is a parameter on the track selector. Applying it takes effect on the next segment
and produces no re-prepare, no re-buffer and no black screen. So when we build the equivalent of your
SP-0071 - it is now ticketed on our side, along with the rung memory - we will deliberately **not** copy
your wait ladder, because our probe costs approximately nothing and yours costs up to 18 s of picture.

Two things follow for you:

- **Your measured table is not evidence that 5 min is the right base.** It is evidence that 60 s is the
  wrong base *when a probe costs a re-open*. The 108.9 s -> 36.0 s black-screen improvement is mostly the
  re-open cost you stopped paying, not information you gained. Worth stating in the doc, because the
  number currently reads as a property of the rule.
- **Your §8 entry "applying a remembered ceiling at open time" is filed as a candidate with a fair
  objection - "wrong on a source that was fixed".** But if every correction costs a re-open, opening at
  the remembered rung is the only way to be right *cheaply*, and the "source was fixed" case is exactly
  what a probe is for. The objection argues for pairing the two, not for skipping the first half.

If a runtime ceiling turns out to be reachable (§1), this section reverses: the re-open goes away and your
existing schedule becomes the over-conservative one.

## 2a. The probe verdict has a hole, and your own event stream closes it

This one is not a suggestion from our side - it came out of reading your §5 against our code, and it applies
to both of us equally. Neither player has it today.

**The ceiling is not the rung.** Setting `:adaptive-maxwidth/maxheight` does not choose a rendition; it
bounds what the adaptive logic may choose. Under that bound the engine keeps picking on its own, at
runtime, without a re-open. So "which rung the picture is actually on right now" is a *different* quantity
from the ceiling - and neither of our players tracks it.

**Where it hurts, specifically: §5's probe-success rule.** "A probe is a success once it survives one full
starvation window." But raising the ceiling does not oblige the adaptive logic to climb. On a link that is
still poor it may stay exactly where it was, and then the session survives 120 s and the rung is recorded
as deliverable - having never been played for a single second. The memory that outlives the window is then
recording a rung the source never delivered. The same hole runs the other way on a step *down*: if the
engine was already below the new ceiling, the step changes nothing, while the log reports it as applied -
which is the failure mode your own §7 exists to prevent.

**The signal is free on both sides.**

- **libVLC:** a runtime representation switch surfaces as `ESAdded`/`ESSelected` with a new video ES
  carrying a different resolution. Reported from your side; we cannot verify it here, but it is worth
  confirming against a session log you already have - if those events are in there, the feature is
  subscription-only work.
- **Media3 (verified):** `AnalyticsListener.onDownstreamFormatChanged(EventTime, MediaLoadData)` - "called
  when the downstream format sent to the renderers changed" - with `MediaLoadData.trackFormat` carrying
  width, height and bitrate. Checked against `media3-exoplayer-1.2.1-sources.jar`,
  `AnalyticsListener.java:906`. We do not subscribe to it today either.

**Three consequences worth acting on, in this order:**

1. **Put the actual rung in `STATS` / `PLAYBACK QUALITY` next to the ceiling.** Without it, `disp_fps` is
   not interpretable - 24 fps is healthy on the bottom rung and a fault on the top one, and one number
   cannot distinguish them. Your §1 calls rendered frame rate the single most useful number; this is the
   field that gives it a denominator.
2. **Make the probe verdict conditional on the engine having actually climbed.** A probe that never left
   the lower rung is not a failure and not a success - it is *no evidence*, which by your own §1
   convention must not be recorded as "fine".
3. **Step down from the playing rung, not from the current ceiling.** Otherwise the first step (and
   possibly several) is a no-op charged to the starvation budget.

We have ticketed all three on our side; item 3 turned out to be a defect in shipped behaviour rather than
a missing feature.

## 3. Give the channel outcome a third state

Your §6 signal-health stripe is session-scoped, and your §7 recommends recording a per-channel outcome.
We keep that record in a small table, and the part worth copying is that it has three values, not two:
OK, FAIL, and UNKNOWN. UNKNOWN is written by the background reachability sweep when the probe could not
conclude - it renders amber, and it never overwrites a real user-initiated result.

The reason is your own SP-0041 in persistent form. A reachability sweep run on a bad link will mark half a
2 300-channel catalogue dead, and a two-valued record cannot represent "we looked and learned nothing".
Once the third state exists, the sweep becomes safe to run unattended, which is the thing that makes the
whole per-channel record worth having.

We also just ticketed our own version of the bug SP-0041 protects you from and we do not: our terminal
dialog offers "remove this channel" unconditionally, including when the user's Wi-Fi is simply off. You
solved that before we did.

## 4. Status must not be inferrable from a path

Your §11 records that three tickets sit in `DONE/` with headers saying Tactical, BlockNeedUserTest and
Implemented, and resolves it by declaring the header authoritative. That is the right call and it will
drift again, because the folder is still saying something.

Our shape, offered as a shape rather than as scripts: status lives in a journal that only a CLI may write;
every status mutation also rewrites the `**Status:**` line in the spec file itself, so the two cannot
diverge; and location carries no status at all - a spec file stays where it is for its whole life, and
archival is a status value, not a move. There is nothing for a folder to contradict.

The cheap version of this, if you want one change rather than a system: stop moving files into `DONE/`.
Let the header be the only place status is written, and the disagreement becomes impossible instead of
merely documented.

## 5. The layer you cannot copy, so you know what you are compensating for

Media3 lets us declare a target distance from the live edge and a speed corridor: we ask for 10 s behind
live, permit 4-20 s, and allow the player to run up to 1.02x to close a gap. The player then holds that
distance by nudging the playback rate, continuously and inaudibly.

libVLC has no equivalent, and that is the honest reason your §3 is long and our clock handling is nearly
empty: `--clock-jitter` is a compensation budget for a timing problem that a live-offset controller does
not let accumulate in the first place. This is not portable advice - there is no option to set. It is
context for two decisions:

- It is worth checking whether `FlyleafVideoBackend`, being FFmpeg-based and yours to extend, can carry a
  rate-adjust loop against a target offset. That is a real feature, not a flag.
- Your §3 "still open" entry on `--no-drop-late-frames` should stay open. Late-frame policy is the
  downstream symptom of not controlling the offset; tuning it before the clock is settled is the same
  class of mistake you already recorded twice in that section.

---

## 6. What we are taking from you

Reciprocity, and it is the larger half of the exchange. Six items from your document are now tickets on
our side:

- **The two-signal freeze rule.** Our watchdog judges by media position alone - the signal you
  deliberately demoted to a fallback. Your §3 is the clearest statement we have found of why that is
  wrong on a sliding live window, and it lands directly in an open bug of ours where a stream re-anchored
  34 times in 11 minutes on a car head unit.
- **The starvation window.** Our step-down counter has the same hysteresis threshold as yours and no time
  window at all, so two stalls an hour apart cost quality. Your 120 s is the missing half of the rule.
- **Escalate by frequency, not by an absolute counter.** You hit the same trap we did - the budget resets
  on a return to live, so the attempt counter never grows - and you solved it with a second layer rather
  than by fixing the counter. That is the part we would not have got to on our own.
- **The connectivity probe before offering to delete a channel** (§3 above).
- **Per-rung failure memory that outlives the session**, with the rung's own escalating wait.
- **"The rule did not apply" and "the rule never ran" must never look the same in an archive.** This is
  the best sentence in the document and it is not about playback. We are adopting it as a general rule.

Two of your conventions we are adopting wholesale, and they are why we can act on the rest: **time is a
parameter, never ambient**, and **null means no evidence, never fine**. Our freeze detector is welded to
a platform timer and a live player, so its correctness cannot be tested at all - the open bug above is
blocked waiting for a log from a specific car radio, which is a sentence that should not need to exist.
Your `Core` split is the fix, and we have ticketed it.

---

## 7. One caution about your own document

Your §1 warning that input bytes are dead on HLS is correct and important, and it is **engine-specific** -
it is an artefact of libVLC filling that counter from the access module while the HLS demuxer fetches
below it. On Media3 the transfer listener sits on the segment fetches, so the same counter is live. Since
that document is also read by an Android developer, the sentence is worth one qualifier, or it will be
carried across as a platform law and cost someone a day.

Separately: §10's before/after table still has no measured row for the `--clock-jitter=1000` change, and
you say so. Your own §1 names the metric that would fill it - the rendered frame rate, differenced over
wall time. That number is the one thing in your instrumentation that needs no assumption about what any
counter means, and the residual you described (156 lost clock references, 63 silence fills over 19 min)
is exactly the shape it would show. Worth running before the next revision of §3.
