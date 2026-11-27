# CLAUDE.md

> **Cross-project contracts live outside this repository.** The shared contract store is `P:\Contracts`, and this line is the one place here that names its location - everywhere else cites a contract by id and document (`STREAM-BANK item D`, `11_live_broadcast_contract.md` section 6), never by drive letter, because whoever clones this repository has no `P:`. `Contracts/README.md` is the index and carries the rules a change to a contract must follow; `Contracts/_meta/REGISTRY.md` is where this product's conformance and its dated exceptions are recorded. Inside this repo, [docs/contracts/](docs/contracts/README.md) holds one pointer per contract - id, version, home, role - and nothing else: a repo-local copy of a contract is the failure the store exists to prevent. **This repository owns five of them** - `APP-BEHAVIOUR` and `APP-STYLE` (the portfolio's desktop UX rules, authored here on 2026-09-22), `USER-PLAYLIST`, `DIAGNOSTIC-REPORT` and `WINDOWS-STORE`; the list is kept in [docs/contracts/README.md](docs/contracts/README.md), never here. A change to how this app treats a dialog, a long operation, a destructive action, the theme, a playlist file, a diagnostic report or a Store package is a change to a contract other binaries are declared against: it is written in the store first and in this code second.

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

StreamsPlayer is a Windows desktop application (.NET 10, WPF) for internet radio, live video, and RTSP. It is an independent product owned by Serhii Zhyhunenko (`SerZhyAle`). `AGENTS.md` is the authoritative contributor guide. The import below puts it in context, so a "read `AGENTS.md`" instruction in an agent or a skill is already satisfied - do not read it a second time.

@AGENTS.md

## Claude Code

- Rule files under `.claude/rules/` load by their `paths:` list; the table in `AGENTS.md` names them. A command that touches an area without reading one of its files (a build, a release) still needs that rule file opened first.
- Read the applicable `docs/agent/` method document before non-trivial work.

**Communication** has one home in the canon - `AUTHOR.md` "Language" and `AI_USAGE.md` §7. No repo-local addition. In particular, do **not** prefix replies with a clock time: `AI_USAGE.md` §7 explicitly supersedes the prompt-timestamp rule this file used to carry.

- **Canon.** The portfolio-wide SZA Unified Rules ship as the `sza` Claude Code plugin and are the source of truth for universal conventions. Consumption model is **reference** - they are not re-authored here (see `AGENTS.md` -> "SZA Unified Rules (canon)"). Load a skill rather than the whole canon: `sza:release`, `sza:store-publish`, `sza:feature-to-site`, `sza:spec-to-audit`, `sza:adopt-canon`, `sza:agent-cost`, `sza:caveman`. Repo overlay facts and legitimate divergences are recorded in the canon's `contrib/streams_player.md`; adoption is stamped in `.sza-canon.json`. Universal-rule fixes land in the canon first.
- **Canon hooks.** The plugin enforces its own tool-call rules; this repo registers **no hooks of its own** - do not add a local copy of one the canon ships (list: [.claude/rules/agent-workflow.md](.claude/rules/agent-workflow.md)).

## Key data-flow contracts

The short form is the section of the same name in `AGENTS.md`; the full text is [.claude/rules/catalog-data.md](.claude/rules/catalog-data.md).
