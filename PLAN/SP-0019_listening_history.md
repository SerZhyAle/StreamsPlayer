# SP-0019: Local listening history

**Status:** Approved

## Goal

Provide a private, bounded list of recently played channels and the last ICY now-playing text observed for each successful listening session.

## Why

History makes it easy to return after an accidental switch and gives useful context without requiring favorites, an account, or remote tracking.

## Non-goals

- Build a full track database or infer song identity.
- Record grid previews, reachability probes, failed launches, or passive catalog browsing.
- Upload, synchronize, or share history automatically.
- Reopen a deleted channel from a stale raw URL.

## Constraints

- A history entry is created only after a user-initiated stream reaches the existing successful-play state.
- Repeated plays update recency rather than creating an unbounded event log; at most 100 channel entries are retained.
- Each entry stores the channel identity, last successful time, and optional last ICY display text; later ICY changes update that entry without adding rows.
- Deleted or pruned channels remain as non-playable history labels until evicted or cleared and fail soft if selected.
- The user can clear all history explicitly; history remains local application data.

## Acceptance criteria

1. Successful user playback places the channel at the top of Recently played with a local timestamp.
2. Preview/probe activity and failed play attempts do not create or promote entries.
3. Replaying a channel updates its existing entry, and retention never exceeds 100 channels.
4. When SP-0014 metadata is available, the entry shows the latest observed display text without treating it as verified track identity.
5. Clear history removes all entries without changing channels, pins, collections, play marks, or catalog data.
6. Restart/persistence, retention, deleted-channel, privacy, and localized run-and-observe checks pass.

## Risks

History is user-sensitive local data. Its scope must remain obvious and bounded, and stale entries must not bypass the current channel list or URL safety rules.

## Dependencies

SP-0014 is required only for optional track text; channel history itself can be delivered independently.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md).
