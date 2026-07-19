---
name: streamplayer-quick
description: Apply one trivial deterministic StreamPlayer edit such as a typo, one string, or one local constant. Do not use for behavioural or design changes.
---
For StreamPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Confirm the edit is isolated and has no design or behaviour decision.
2. Make only that change; do not create a ticket or refactor nearby code.
3. Inspect the changed line or run the smallest relevant static check.
4. Report the one-line change and evidence.

