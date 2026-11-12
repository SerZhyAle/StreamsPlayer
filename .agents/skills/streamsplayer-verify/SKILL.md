---
name: streamsplayer-verify
description: Build and observe a minimal StreamsPlayer scenario, including WPF UI or the catalog harness when appropriate, and report PASS or FAIL evidence.
---
For StreamsPlayer, follow `AGENTS.md`, the applicable `docs/agent/` method document, and the task’s lowest sufficient complexity rung. Keep reports concise and record command checks as `expected: ... | actual: ...`.

1. Select the smallest run command that exercises the changed behaviour.
2. Build first when necessary, then run the WPF app or catalog harness and walk the scenario. A build is release tier (`docs/agent/VALIDATION.md`): between releases it runs only when the owner names the run or at the release itself; otherwise report the scenario as a `RELEASE` item with the command the release runs.
3. Store screenshots, logs, or outputs under `temp/<ticket>/` or `artifacts/`.
4. Report only PASS/FAIL, the observed result, and evidence path. Do not alter ticket status.
