---
name: streamplayer-verify
description: Build and observe a minimal StreamPlayer scenario, including WPF UI or the catalog harness when appropriate, and report PASS or FAIL evidence.
---
For StreamPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Select the smallest run command that exercises the changed behaviour.
2. Build first when necessary, then run the WPF app or catalog harness and walk the scenario.
3. Store screenshots, logs, or outputs under `tmp/` or `artifacts/`.
4. Report only PASS/FAIL, the observed result, and evidence path. Do not alter ticket status.

