---
paths:
  - ".claude/commands/**"
  - ".claude/agents/**"
  - ".agents/**"
  - ".codex/**"
  - "docs/agent/**"
  - "memory/**"
  - "PLAN/**"
  - ".sza-profile.json"
  - ".sza-canon.json"
---

# Agent workflow, tickets and memory

Detail moved verbatim from `CLAUDE.md` and `AGENTS.md` on 2026-11-25; the always-loaded core is `AGENTS.md`.

## Workflow tooling

This repo runs the Universal Agent Kit method. **`AGENTS.md` is the authoritative rules file** - read it and the applicable `docs/agent/` document before non-trivial work. The repo-local shape of the method: split *what/why* (`/streamsplayer-spec`) from *how* (`/streamsplayer-spec-tech`), and reach for the cheapest sufficient rung on the `/streamsplayer-*` ladder. The evidence rule, the cost discipline and the research-first rule are the canon's, not this file's.

**Repo-local evidence delta**: a changed GUI action needs run-and-observe evidence, not merely a build (`docs/agent/VALIDATION.md`).

- **Skills.** The same procedures are available three ways: native Claude Code slash commands in [.claude/commands/](../commands/) (`/streamsplayer-quick`, `-fix`, `-research`, `-spec`, `-spec-tech`, `-spec-dev`, `-spec-check`, `-spec-fix`, `-spec-all`, `-backlog`, `-park`, `-ui-clarify`, `-verify`, `-review`, `-git`, `-caveman[-commit|-review]`), Codex/`$`-invoked skills in [.agents/skills/](../../.agents/skills/), and the shared `SKILL.md` bodies both point to. The `.claude/commands/*` files are thin wrappers - the procedure lives in `.agents/skills/streamsplayer-*/SKILL.md`.
- **Agents.** Role subagents in [.claude/agents/](../agents/) (`streamsplayer-rd-lead` is the default orchestrator, plus `-solution-researcher`, `-implementer`, `-doc-writer`), mirrored from [.codex/agents/](../../.codex/agents/).
- **Method docs.** [docs/agent/](../../docs/agent/): `SPEC_LIFECYCLE`, `CODE_QUALITY`, `VALIDATION`, `RESEARCH_INDEX`, `AGENT_MEMORY`, `COST`, `CODE_AUDIT` (auditing unchanged code in slices; tools under `tools/audit/`).
- **Tickets.** Spec-driven planning under `PLAN/` (gitignored - local to the maintainer, never in a clone): `STRWIN-NNNN` ids, states `Draft → Approved → Tactical → In Progress → Implemented → Verified` (+ `Partial`/`Broken`/`Block*`). Status comes from the working tree, never the filename. Verified strategic tickets and their tactical folders move to `PLAN/DONE/`.
- **Memory.** File-based, committed, shared across tools: [memory/MEMORY.md](../../memory/MEMORY.md) is the always-loaded index (types: `user`, `feedback`, `project`, `reference`); discipline in `docs/agent/AGENT_MEMORY.md`.
- **Canon hooks.** The plugin enforces five behaviours at the tool call, so they are not restated as prose here: the Bash-safety family (a disk-wide `find`, a `.ps1` or a PowerShell cmdlet in Bash command-head position, a missing interpreter, a slash-argument MSYS mangling), a refusal to background a gate or a closure facade, and a rewrite of an uncapped large read. This repo registers **no hooks of its own** - do not add a local copy of one the canon ships.

### Skill routing

- `$streamsplayer-quick`: one trivial deterministic edit; `$streamsplayer-fix`: narrow, understood behaviour bug.
- `$streamsplayer-research`: evidence-first investigation before non-trivial work.
- `$streamsplayer-spec` -> `$streamsplayer-spec-tech` -> `$streamsplayer-spec-dev` -> `$streamsplayer-spec-check`: changes with real design decisions.
- `$streamsplayer-spec-fix`, `$streamsplayer-spec-all`, `$streamsplayer-ui-clarify`, `$streamsplayer-verify`, `$streamsplayer-review`, `$streamsplayer-git`, `$streamsplayer-park`, `$streamsplayer-backlog`, and `streamsplayer-caveman*` follow their named procedures.

### Specifications, quality, and validation

- Ticket IDs use the prefix `STRWIN`: `STRWIN-0001`, `STRWIN-0002`, and so on (`.sza-profile.json` `grammar.ticketIdPattern`). Strategic tickets are `PLAN/STRWIN-0001_slug.md`; tactical plans live under `PLAN/STRWIN-0001_slug/`. The numbers continue the former `SP-####` series (migrated 2026-11-19, canon TASK-FLOW 0.37): a ticket that was still open then keeps its number under the new prefix and lists its old id in `formerIds` of the ledger, a closed ticket keeps its `SP-####` id, and an old id stays a valid name of its ticket.
- Status comes from reality: `Draft -> Approved -> Tactical -> In Progress -> Implemented -> Verified`, with `Partial`, `Broken`, `Archived`, and documented `Block*` states. Strategic specs contain what/why; tactical plans contain dependency-ordered implementation steps with static checks.
