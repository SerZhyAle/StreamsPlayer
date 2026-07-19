# StreamPlayer Spec Lifecycle

This repository uses the Universal Agent Kit method: agree on *what and why* before documenting *how*.

## Ticket states

`Draft -> Approved -> Tactical -> In Progress -> Implemented -> Verified`

An audit may instead yield `Partial` or `Broken`. Use `BlockNeedUserTest`, `BlockByOtherTask`, `BlockQuestions`, or `BlockExternal` only with a one-line reason and a clear exit condition. `Archived` preserves cancelled work without deleting its record.

The first `**Status:**` line is authoritative only when the working tree and checks prove it.

## Complexity ladder

- Trivial deterministic edit: use `$streamplayer-quick`; no ticket.
- Narrow, understood bug: use `$streamplayer-fix`; local validation.
- Small deterministic change (at most three existing files, no public type, schema, dependency-wiring, or new screen): write a primitive ticket with Problem, Approach, and Done criteria.
- Any design uncertainty or broader impact: research, strategic spec, tactical plan, execution, audit.

## Ticket layout

- Strategic spec: `PLAN/SP-0001_slug.md`.
- Tactical plan: `PLAN/SP-0001_slug/INDEX.md` plus ordered phase files.
- Strategic specs contain goals, constraints, criteria, and unresolved questions; no classes or paths.
- Tactical steps contain exact affected paths/symbols, dependency order, and a static verification predicate. A step is not done until its predicate passes in that run.

## Workflow gates

1. `$streamplayer-research` gathers cited evidence without changing ticket status.
2. `$streamplayer-spec` produces an approved strategic spec after genuine questions are resolved.
3. `$streamplayer-spec-tech` turns it into dependency-ordered, verifiable phases and marks it Tactical.
4. `$streamplayer-spec-dev` executes one checked step at a time and reaches Implemented or a documented block.
5. `$streamplayer-spec-check` audits live code against the ticket and marks Verified, Partial, Broken, or BlockNeedUserTest.
6. `$streamplayer-spec-fix` applies mechanical audit actions, then re-audits.

`Verified` requires zero failed, warning, or open manual criteria. A GUI/manual check stays blocked until it is actually observed.

