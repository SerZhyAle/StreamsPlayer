---
name: streamsplayer-research
description: Perform an evidence-first, read-only investigation of a StreamsPlayer feature area, bug, architecture question, or external contract before a non-trivial change.
---
For StreamsPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Read `README.md`, relevant `PLAN/` ticket, and `docs/agent/RESEARCH_INDEX.md`.
2. Locate symbols with `rg` before opening implementation files. Read tests and contracts alongside code.
3. Use official documentation only for version-specific gaps.
4. Write a concise cited dossier under the ticket directory or `tmp/`; include current flow, reusable patterns, constraints, open questions, and risks.
5. Do not modify production code or present guessed paths as evidence.

