---
name: streamsplayer-review
description: Review current StreamsPlayer changes for correctness, regression risk, architecture, anti-slop patterns, and missing validation.
---
For StreamsPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Review the current diff or explicitly named files; default to changed scope, not the entire repository.
2. Prioritize correctness, Core/WPF boundaries, catalog-contract preservation, error handling, tests, and user-visible behaviour.
3. Report actionable findings first, ordered by severity, with file and line references.
4. Do not add praise padding or invent findings.
