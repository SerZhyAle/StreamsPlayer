# StreamsPlayer Agent Memory

Short index of durable, non-obvious context for future sessions. Add one link per entry; keep entry bodies in separate files and verify repo claims before relying on them.

## User

## Feedback

- When a task statement is meaningfully ambiguous, ask the user to clarify it
  before choosing an interpretation that could change the expected result.

## Project

- Catalog text search (`ApplyFilter`, `MainWindow.xaml.cs`) intentionally matches
  Title **OR** Topic **OR** Language. Channels whose *category/topic* matches (e.g.
  "Sports") appear even without the term in their name — this looks "unfiltered"
  but is by design. User confirmed keeping the broad match (2026-07-20). Do not
  narrow it to name-only without a new product decision.
- After a strategic `PLAN/SP-NNNN_*.md` ticket reaches `Verified`, move that
  ticket file and its same-named tactical-plan folder, when present, to
  `PLAN/DONE/`. Keep active, blocked, Draft, Approved, Tactical, In Progress,
  Implemented, Partial, and Broken tickets in `PLAN/`; update any affected
  local links when moving a verified ticket.

- Live recovery (SP-0015): the retry policy is a pure Core state machine
  (`LivePlaybackRecoveryPolicy` + `PlaybackRecoveryClassifier`); App backends feed
  `PlaybackFailureSignal` and apply decisions. Three non-obvious design points to preserve:
  (1) LibVLC and WPF `MediaElement` hide the HTTP status, so 429/5xx-vs-non-429-4xx classification
  needs a failure-path-only probe (`PlaybackStatusProbe`, http/https only, never on grid previews);
  (2) budgets are *consecutive* and reset on sustained live (`NotifyLive`), which is what keeps
  looping-playlist EndReached streams from exhausting the budget — do not make budgets lifetime;
  (3) Part D's stall-watchdog and the tuning-doc rule "never reconnect to grow the buffer" are
  reconciled by reconnecting only on a *silent freeze* (position frozen ~9 s while nominally playing,
  gated on `_reachedLive`) or buffering > 15 s with no position progress — genuine rebuffering is left
  in place. See `PLAN/SP-0015_resilient_live_recovery.md`.

## References

- Toolbar glyph icons: `App.xaml`'s shared `GlyphButton` template applies **both**
  `Fill` and `Stroke` = Foreground to the swapped `GlyphGeometry`, so any closed/near-closed
  path renders as a solid silhouette (fine for a gear or eye, wrong for an outline shape like
  a clock face whose hands would vanish). For an outline icon, give the style its own
  `ContentTemplate` with `Fill="Transparent"` instead of only swapping `GlyphGeometry` —
  see `HistoryGlyphButton` (SP-0019). Confirmed 2026-07-22.
