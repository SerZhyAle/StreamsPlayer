# StreamsPlayer Code Quality

Apply these rules to new and changed code. Do not start a legacy-wide cleanup unless the task asks for it.

- Comments explain non-obvious reasoning, invariants, or workarounds—not the adjacent statement.
- Catch the narrowest exception and recover, return a documented safe default, or rethrow. Never silently swallow exceptions.
- Reuse existing constants and project values rather than duplicating recurring strings, URLs, colours, or magic numbers.
- Keep asynchronous work owned by its UI or operation lifetime; do not introduce global mutable state as a shortcut.
- App and Core require a deliberate logging facade before new diagnostic logging. The CatalogHarness may write its contract output to the console.
- Do not leave reachable `TODO`, `NotImplementedException`, placeholder UI actions, unused types, or obsolete resources behind.
- Preserve the Core boundary: no WPF dependencies in `StreamsPlayer.Core`.
- Keep windows thin and extract cohesive helpers around the ~500-line budget.

Review diffs for these patterns before completion. When a violation recurs, add a precise preventive rule to `AGENTS.md` rather than only catching it after generation.

