---
paths:
  - "docs/**"
  - "tools/site/**"
  - "tools/store/**"
  - "tools/help/**"
  - "help/**"
  - "tools/Build-CliDoc.ps1"
  - "tools/Test-CliDoc.ps1"
  - "scripts/check-docs.ps1"
  - "scripts/check-help.ps1"
  - "POSITIONING.md"
  - "DOCS_SURFACES.md"
  - "DOCUMENT_REGISTRY.jsonl"
  - "README*.md"
---

# Documentation, site and generators

Detail moved verbatim from `AGENTS.md` on 2026-11-25; the always-loaded core is `AGENTS.md`.

## Project layout (tooling and documents)

- `tools/site/` and `tools/store/` - generators for the GitHub Pages site and the Store listing/screenshot pipeline. `tools/site/site-facts.json` declares the facts the site copy states and the surfaces bound to `POSITIONING.md`.
- `POSITIONING.md` - the positioning source: what the product is and its ordered pillars; the README, the site and the Store listing are written from it.
- `tools/audit/` - the code-audit campaign tools: slicer, slice-ticket fan-out, campaign summary and their fixture tests (`docs/agent/CODE_AUDIT.md`).
- `scripts/` - the release-parity check, the release checklist, and the commit-and-build helper.
- `docs/contracts/` - one pointer per shared contract this product consumes or owes: id, version, home, role.
- `docs/agent/` - agent workflow and validation guidance.
- `docs/` and `assets/` - GitHub Pages and product documentation assets.
- `DOCS_SURFACES.md` - the ship-together list of every documentation surface: source, generator, languages.
- `memory/` - the committed agent memory index.
- `.github/` - CI, release automation, and contribution templates.
- `msix/` - Store-ready package template and package-build guidance.
- `winget/` - release-manifest templates and submission notes; `manifests/` holds a version-pinned copy of a submitted manifest set and is not the template source.
- `PLAN/` - spec tickets. **Untracked** (`.gitignore`): a fresh clone, CI, or an outside contributor will not have it, so nothing outside this working tree may depend on a ticket's contents.

## Documentation commands

- `pwsh -NoProfile -File ./tools/site/build-site.ps1 -Check` - fail if `docs/` is stale against the generator, a held site address does not resolve, or a fact the copy states (language count, minimum Windows, channels) or a pillar of `POSITIONING.md` disagrees with its source; writes nothing. The language count and the minimum Windows are placeholders (`[[languages]]`, `[[windows]]`) in the copy decks, never typed.
- `pwsh -NoProfile -File ./scripts/check-docs.ps1` - the documentation gate (`DOC-INTERNAL-QUALITY`, SP-0140): `DOCUMENT_REGISTRY.jsonl` declares the roots of the maintained corpus (`{"root":"<directory>/","reason":..}` or `{"root":"<file>","reason":..}`, SP-0287) and its reasoned ignores, and every `*.md` and generated `docs/**/*.html` under a root is declared there; a file under no root is outside the corpus, named in a `note:` line and not judged. Relative links, anchors and images resolve to tracked files, house style in prose, no `http://` or remote embeds. A new document needs its registry entry in the same change, and a new documentation folder needs its root record. It also holds the portal contribution (`docs/portal/streamsplayer*.md`, `DOC-EXTERNAL-QUALITY`): every **bold** or «guillemet» control name must be exactly a string of that locale's `Localization.<code>.xaml`, and the registry entry of each ru/uk mirror records `mirrorOf` and `mirrorOfHash`, the fingerprint of the English page it was rebuilt from - change the English page and the mirror fails until it is rebuilt and the hash re-recorded.

- `pwsh -NoProfile -File ./tools/help/build-help.ps1` (`-Check` writes nothing and fails on a stale file) - regenerate `docs/help/` from `help/` and the dictionaries; `pwsh -NoProfile -File ./scripts/check-help.ps1` - hold the generated help pages to `CONTEXT-HELP` (SP-0230). Both run inside `scripts/check.ps1`.

- `pwsh -NoProfile -File ./tools/Build-CliDoc.ps1` (`-Check` writes nothing and fails on a stale file or a manifest entry that lacks a member of `APP-COMMANDS` rule 18) - regenerate the command-line reference from `docs/cli-commands.json` (SP-0276); never edit the generated file by hand, and extend the manifest when a command ships, then run this.
