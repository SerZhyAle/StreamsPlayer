---
name: streamsplayer-ui-clarify
description: Resolve meaningful user-facing ambiguity in StreamsPlayer WPF UI, CLI output, errors, labels, or fallback behaviour before implementation.
---
For StreamsPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Identify user-visible decisions: placement, wording, default, empty/error state, accessibility, and manual-test expectation.
2. Derive settled answers from the existing app and product specification.
3. Present only genuine product judgement calls with a recommended default.
4. Record resolved decisions in the active ticket before code changes.
