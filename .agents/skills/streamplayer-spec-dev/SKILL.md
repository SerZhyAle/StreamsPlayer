---
name: streamplayer-spec-dev
description: Execute a StreamPlayer tactical plan one verified step at a time without guessing beyond its approved scope.
---
For StreamPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Read the strategic ticket, tactical index, and first unfinished step.
2. Execute exactly one dependency-ready step, then run its stated verification before marking it complete.
3. Keep Core platform-neutral and preserve explicit catalog refresh plus MANUAL/IMPORTED merge protection.
4. Hard-stop and record a Block state on ambiguity, failed verification, or unmet external condition.
5. Update status to In Progress, Implemented, or BlockNeedUserTest only when the live result justifies it.

