---
name: streamplayer-spec-fix
description: Apply only mechanical action items from a StreamPlayer specification audit, then rerun the audit.
---
For StreamPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Read the ticket’s latest audit and identify action items requiring no product decision.
2. Apply the smallest fixes and their narrow checks.
3. Leave judgment calls as explicit blockers rather than guessing.
4. Invoke the audit procedure again and update status from its evidence.

