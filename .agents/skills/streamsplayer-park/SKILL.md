---
name: streamsplayer-park
description: Capture a real but out-of-scope StreamsPlayer finding as a minimal Draft ticket, then return to the active task without fixing it.
---
For StreamsPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Allocate an SP identifier and create a minimal Draft ticket with the observed symptom, location, and why it is out of scope.
2. Do not investigate or implement the parked finding beyond enough evidence to make it actionable.
3. Return `parked: SP-NNNN` and resume the original task.
