---
name: streamplayer-spec-check
description: Audit a StreamPlayer implementation against its strategic and tactical ticket using the live working tree and concrete checks.
---
For StreamPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Read the ticket, acceptance criteria, tactical steps, changed code, and relevant tests.
2. Compare intended behaviour to evidence in the live tree, never to a filename or status alone.
3. Append or replace a compact `## Last Audit` section with PASS, WARN, FAIL, and MANUAL evidence.
4. Set Verified only with no open items; otherwise set Partial, Broken, or BlockNeedUserTest.

