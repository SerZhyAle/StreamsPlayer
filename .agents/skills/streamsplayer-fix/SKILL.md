---
name: streamsplayer-fix
description: Fix a narrow, understood StreamsPlayer behaviour bug with a bounded blast radius and focused validation. Do not use for new design work.
---
For StreamsPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Cite the observed symptom, current path, and root cause before editing.
2. Fix the bug, not the surrounding neighbourhood. Add or adjust a focused test when it is practical.
3. Run the narrowest meaningful check. Between releases that is static inspection, `scripts/check-fast.ps1` and a direct call of what changed; a build or a test run waits for the release unless the owner names it, and is reported as a `RELEASE` item (`docs/agent/VALIDATION.md`).
4. Report root cause, fix, and validation in one line each.
